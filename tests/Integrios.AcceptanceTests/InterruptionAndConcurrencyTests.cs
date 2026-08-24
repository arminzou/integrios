using Integrios.Tests.Shared;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Integrios.AcceptanceTests;

[Collection(PackagedDeploymentCollection.Name)]
[Trait("Category", "ResilienceQualification")]
public sealed class InterruptionAndConcurrencyTests(PackagedDeploymentFixture fixture)
{
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LeaseRecoveryTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan CleanupLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly Guid HttpConnectorId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ApiKeyConnectorId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // Barrier key for the post-send window. Any constant works; it only has to be unique within
    // this deployment, and both the test session and the trigger must agree on it.
    private const long PostSendBarrierKey = 8912;

    [Fact]
    public async Task CompetingWorkers_FanOutWithoutLossOrDuplication()
    {
        var workers = new List<string>();
        bool workerStopped = false;

        try
        {
            string suffix = Suffix();
            Pipeline pipeline = await CreatePipelineAsync($"stress-{suffix}", authReference: null);
            const int eventCount = 40;

            await fixture.KillWorkerAsync();
            workerStopped = true;

            for (int index = 0; index < eventCount; index++)
                await IngestAsync(pipeline, new { sequence = index }, $"stress-{suffix}-{index}");

            // Widening the fanout update lengthens the window in which two Workers can claim the
            // same outbox row, so the SKIP LOCKED contract is actually exercised rather than won
            // by luck of scheduling.
            await fixture.ExecuteAsync(
                """
                CREATE FUNCTION qualification_slow_fanout() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.processed_at IS NULL AND NEW.processed_at IS NOT NULL THEN
                        PERFORM pg_sleep(0.05);
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER qualification_slow_fanout
                    BEFORE UPDATE ON outbox
                    FOR EACH ROW EXECUTE FUNCTION qualification_slow_fanout();
                """);

            workers.Add(await fixture.StartAdditionalWorkerAsync());
            workers.Add(await fixture.StartAdditionalWorkerAsync());

            await WaitForAsync(async () =>
                await fixture.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM event_deliveries sd JOIN events e ON e.id = sd.event_id WHERE e.tenant_id = '{pipeline.TenantId}' AND e.event_type = '{pipeline.EventType}' AND sd.status = 'succeeded'") == eventCount);

            Assert.Equal(eventCount, await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM events WHERE tenant_id = '{pipeline.TenantId}' AND event_type = '{pipeline.EventType}' AND status = 'routed'"));
            Assert.Equal(eventCount, await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM outbox o JOIN events e ON e.id = o.event_id WHERE e.tenant_id = '{pipeline.TenantId}' AND e.event_type = '{pipeline.EventType}' AND o.processed_at IS NOT NULL"));
            Assert.Equal(0, await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM (SELECT sd.event_id, sd.subscription_id FROM event_deliveries sd JOIN events e ON e.id = sd.event_id WHERE e.tenant_id = '{pipeline.TenantId}' AND e.event_type = '{pipeline.EventType}' GROUP BY sd.event_id, sd.subscription_id HAVING COUNT(*) > 1) duplicates"));

            foreach (string worker in workers)
                Assert.Contains("Fanned out Event", await fixture.GetContainerLogsAsync(worker), StringComparison.Ordinal);
        }
        finally
        {
            foreach (string worker in workers)
                await SafeRemoveContainerAsync(worker);

            // Removing a container leaves its PostgreSQL backend behind, so a Worker interrupted
            // mid-fanout can still hold the outbox row lock that the cleanup DDL needs. Only safe
            // while the primary Worker is down; otherwise this would terminate live statements.
            if (workerStopped)
                await TerminateActiveBackendsAsync("outbox");

            await SafeExecuteAsync(
                """
                DROP TRIGGER IF EXISTS qualification_slow_fanout ON outbox;
                DROP FUNCTION IF EXISTS qualification_slow_fanout();
                """);

            if (workerStopped)
                await fixture.StartWorkerAsync();
        }
    }

    [Fact]
    public async Task PostgresRestartDuringRetry_ResumesAndSucceeds()
    {
        string suffix = Suffix();
        Pipeline pipeline = await CreatePipelineAsync($"restart-{suffix}", authReference: null);
        using HttpResponseMessage fail = await fixture.MockSinkClient.PutAsJsonAsync(
            $"/control/{pipeline.SinkName}",
            new { mode = "fail" });
        Assert.Equal(HttpStatusCode.OK, fail.StatusCode);

        Guid eventId = await IngestAsync(pipeline, new { restart = true });
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}' AND da.status = 'failed'") >= 1);

        using HttpResponseMessage reset = await fixture.MockSinkClient.DeleteAsync($"/control/{pipeline.SinkName}");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        await fixture.RestartPostgresAsync();

        // Restarting the product services is part of the interruption window, not a workaround:
        // pooled connections opened before the restart are dead, and the platform has to come
        // back without operator intervention.
        await fixture.RestartProductServicesAsync();

        await WaitForDeliveryStatusAsync(eventId, "succeeded");
        Assert.True(await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}'") >= 2);
    }

    [Fact]
    public async Task PreHttpInterruption_RecoversAndDeliversExactlyOnce()
    {
        bool workerStopped = false;

        try
        {
            await InstallQualificationConnectorAsync();
            await fixture.RecreateWorkerAsync("file");

            string suffix = Suffix();
            string secretReference = $"blocking_{suffix}";
            Pipeline pipeline = await CreatePipelineAsync($"presend-{suffix}", secretReference);

            // A FIFO with no writer blocks the Worker inside secret resolution, which is before
            // any HTTP request is issued and before it holds a database transaction.
            await fixture.CreateBlockingSecretPipeAsync(pipeline.TenantSlug, secretReference);

            Guid eventId = await IngestAsync(pipeline, new { phase = "before-http" });
            await WaitForDeliveryStatusAsync(eventId, "in_flight");
            Assert.Equal(0, await ReceiptCountAsync(pipeline.SinkName));

            await fixture.KillWorkerAsync();
            workerStopped = true;
            await fixture.ReplaceSecretWithFileAsync(pipeline.TenantSlug, secretReference, "recovered-secret");
            await fixture.ExecuteAsync(
                $"UPDATE event_deliveries SET lease_expires_at = now() - interval '1 second' WHERE event_id = '{eventId}' AND status = 'in_flight'");
            await fixture.StartWorkerAsync();
            workerStopped = false;

            await WaitForDeliveryStatusAsync(eventId, "succeeded");
            Assert.Equal(["indeterminate", "succeeded"], await AttemptStatusesAsync(eventId));
            Assert.Equal(1, await ReceiptCountAsync(pipeline.SinkName));
            await AssertReceiptHeadersAsync(
                pipeline.SinkName,
                new Dictionary<string, string> { ["X-Api-Key"] = "recovered-secret" });
        }
        finally
        {
            if (workerStopped)
                await fixture.StartWorkerAsync();
        }
    }

    [Fact]
    public async Task PostSendInterruption_PreservesAtLeastOnceDelivery()
    {
        bool workerStopped = false;
        bool barrierHeld = false;
        await using var barrier = new NpgsqlConnection(fixture.ConnectionString);

        try
        {
            string suffix = Suffix();
            Pipeline pipeline = await CreatePipelineAsync($"postsend-{suffix}", authReference: null);

            // The barrier is an advisory lock rather than a sleep so the test can end it on
            // demand. A sleeping backend cannot be released, and because killing the Worker
            // container does not terminate its PostgreSQL session, it would hold the
            // delivery_attempts row lock that both lease recovery and the cleanup DDL need.
            await barrier.OpenAsync();
            await ExecuteOnAsync(barrier, $"SELECT pg_advisory_lock({PostSendBarrierKey});");
            barrierHeld = true;

            await fixture.ExecuteAsync(
                $$"""
                CREATE FUNCTION qualification_block_success_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status = 'in_progress' AND NEW.status = 'succeeded' THEN
                        PERFORM pg_advisory_xact_lock({{PostSendBarrierKey}});
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER qualification_block_success_finalization
                    BEFORE UPDATE ON delivery_attempts
                    FOR EACH ROW EXECUTE FUNCTION qualification_block_success_finalization();
                """);

            Guid eventId = await IngestAsync(pipeline, new { phase = "after-send" });
            await WaitForAsync(async () => await ReceiptCountAsync(pipeline.SinkName) == 1);
            await WaitForDeliveryStatusAsync(eventId, "in_flight");
            Guid deliveryId = await fixture.ScalarAsync<Guid>(
                $"SELECT id FROM event_deliveries WHERE event_id = '{eventId}'");
            Guid firstAttemptId = await fixture.ScalarAsync<Guid>(
                $"SELECT id FROM delivery_attempts WHERE event_delivery_id = '{deliveryId}' AND attempt_number = 1");

            await fixture.KillWorkerAsync();
            workerStopped = true;

            // Terminating the orphaned backend rolls its finalization transaction back now,
            // instead of whenever it next notices the closed client socket.
            await TerminateActiveBackendsAsync("delivery_attempts");
            await ExecuteOnAsync(barrier, $"SELECT pg_advisory_unlock({PostSendBarrierKey});");
            barrierHeld = false;
            await DropPostSendBarrierAsync();

            await fixture.StartWorkerAsync();
            workerStopped = false;

            // No lease is force-expired here: recovering within the real lease window is itself
            // an acceptance criterion, which is why this scenario takes about two minutes.
            await WaitForDeliveryStatusAsync(eventId, "succeeded", LeaseRecoveryTimeout);
            Assert.Equal(2, await ReceiptCountAsync(pipeline.SinkName));
            Assert.Equal(["indeterminate", "succeeded"], await AttemptStatusesAsync(eventId));

            Guid secondAttemptId = await fixture.ScalarAsync<Guid>(
                $"SELECT id FROM delivery_attempts WHERE event_delivery_id = '{deliveryId}' AND attempt_number = 2");
            await AssertReceiptHeadersAsync(
                pipeline.SinkName,
                new Dictionary<string, string>
                {
                    ["Integrios-Delivery-Id"] = deliveryId.ToString(),
                    ["Integrios-Attempt-Id"] = firstAttemptId.ToString(),
                    ["Integrios-Attempt-Number"] = "1"
                });
            await AssertReceiptHeadersAsync(
                pipeline.SinkName,
                new Dictionary<string, string>
                {
                    ["Integrios-Delivery-Id"] = deliveryId.ToString(),
                    ["Integrios-Attempt-Id"] = secondAttemptId.ToString(),
                    ["Integrios-Attempt-Number"] = "2"
                });
        }
        finally
        {
            if (barrierHeld)
                await SafeExecuteOnAsync(barrier, $"SELECT pg_advisory_unlock({PostSendBarrierKey});");

            // Only while the Worker is down: on the success path it is running again, and
            // terminating its live backend would corrupt later tests in this collection.
            if (workerStopped)
                await TerminateActiveBackendsAsync("delivery_attempts");

            await SafeDropPostSendBarrierAsync();

            if (workerStopped)
                await fixture.StartWorkerAsync();
        }
    }

    private async Task DropPostSendBarrierAsync() => await fixture.ExecuteAsync(
        $"""
        SET lock_timeout = '{(int)CleanupLockTimeout.TotalSeconds}s';
        DROP TRIGGER IF EXISTS qualification_block_success_finalization ON delivery_attempts;
        DROP FUNCTION IF EXISTS qualification_block_success_finalization();
        """);

    private async Task SafeDropPostSendBarrierAsync()
    {
        try
        {
            await DropPostSendBarrierAsync();
        }
        catch (Exception)
        {
            // Cleanup must never replace the assertion failure that brought us here.
        }
    }

    // Killing a container leaves its PostgreSQL backend running, so locks taken by an interrupted
    // Worker outlive it. Matching on an actively executing statement against the table under test
    // is specific enough here: the Worker is already dead, and Admin and Ingress hold no
    // long-running statement against these tables.
    private async Task TerminateActiveBackendsAsync(string table) => await SafeExecuteAsync(
        $"""
        SELECT pg_terminate_backend(pid)
        FROM pg_stat_activity
        WHERE datname = current_database()
          AND pid <> pg_backend_pid()
          AND state = 'active'
          AND query ILIKE '%{table}%';
        """);

    private async Task SafeExecuteAsync(string sql)
    {
        try
        {
            await fixture.ExecuteAsync(sql);
        }
        catch (Exception)
        {
            // Cleanup must never replace the assertion failure that brought us here.
        }
    }

    private async Task SafeRemoveContainerAsync(string containerName)
    {
        try
        {
            await fixture.RemoveContainerAsync(containerName);
        }
        catch (Exception)
        {
            // Fixture teardown remains the final cleanup.
        }
    }

    private static async Task ExecuteOnAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SafeExecuteOnAsync(NpgsqlConnection connection, string sql)
    {
        try
        {
            await ExecuteOnAsync(connection, sql);
        }
        catch (Exception)
        {
            // Disposing the connection releases any session-level advisory lock anyway.
        }
    }

    private async Task<Pipeline> CreatePipelineAsync(string name, string? authReference)
    {
        string tenantSlug = name;
        Guid tenantId = await PostAdminForIdAsync(
            "/admin/tenants",
            new { slug = tenantSlug, name = $"Resilience {name}", environment = "production" });
        string apiToken = await PostAdminForPropertyAsync(
            $"/admin/tenants/{tenantId}/api-keys",
            new { name = "resilience-ingress" },
            "token");
        Guid sourceConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = HttpConnectorId,
                name = "resilience-source",
                config = new { base_uri = $"http://mocksink:8080/sink/{name}-source" },
                environment = "production"
            });

        object? auth = authReference is null
            ? null
            : new
            {
                scheme = "api_key_header",
                config = new { header_name = "X-Api-Key" },
                secret_refs = new { api_key = authReference }
            };
        Guid destinationConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = authReference is null ? HttpConnectorId : ApiKeyConnectorId,
                name = "resilience-destination",
                config = new { base_uri = $"http://mocksink:8080/sink/{name}" },
                destination_authentication = auth,
                environment = "production"
            });
        Guid topicId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics",
            new { name, source_connection_ids = new[] { sourceConnectionId } });
        Guid subscriptionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions",
            new
            {
                name = "resilience-subscription",
                match_rules = new { event_type = $"{name}.test" },
                destination_connection_id = destinationConnectionId
            });

        return new Pipeline(
            tenantId,
            tenantSlug,
            apiToken,
            sourceConnectionId,
            name,
            $"{name}.test",
            subscriptionId,
            name);
    }

    private async Task InstallQualificationConnectorAsync() => await fixture.ExecuteAsync($$"""
        INSERT INTO connectors (
            id, key, contract_version, manifest_schema_version, name, direction,
            supported_auth_schemes, status, description, manifest)
        VALUES (
            '{{ApiKeyConnectorId}}',
            'qualification_resilience_api_key',
            1,
            1,
            'Qualification resilience API key',
            'destination',
            '["api_key_header"]',
            'active',
            'Qualification-only resilience connector',
            '{{TestConnectorManifest.Create(
                "qualification_resilience_api_key",
                "Qualification resilience API key",
                "destination",
                ["api_key_header"])}}'::jsonb)
        ON CONFLICT (id) DO NOTHING;
        """);

    private async Task<Guid> IngestAsync(
        Pipeline pipeline,
        object payload,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(new
            {
                source_connection_id = pipeline.SourceConnectionId,
                topic_name = pipeline.TopicName,
                event_type = pipeline.EventType,
                payload,
                idempotency_key = idempotencyKey ?? $"resilience-{Guid.NewGuid():N}"
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {pipeline.ApiToken}");
        using HttpResponseMessage response = await fixture.IngressClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("event_id").GetGuid();
    }

    private async Task<Guid> PostAdminForIdAsync(string path, object body) =>
        Guid.Parse(await PostAdminForPropertyAsync(path, body, "id"));

    private async Task<string> PostAdminForPropertyAsync(string path, object body, string property)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        using HttpResponseMessage response = await fixture.AdminClient.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST {path} returned {(int)response.StatusCode}: {responseBody}");
        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document.RootElement.GetProperty(property).ToString();
    }

    private async Task WaitForDeliveryStatusAsync(
        Guid eventId,
        string expected,
        TimeSpan? timeout = null) =>
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM event_deliveries WHERE event_id = '{eventId}'") == expected,
            timeout);

    private async Task<string[]> AttemptStatusesAsync(Guid eventId)
    {
        string statuses = await fixture.ScalarAsync<string>(
            $"SELECT string_agg(da.status, ',' ORDER BY da.attempt_number) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}'");
        return statuses.Split(',');
    }

    private async Task<int> ReceiptCountAsync(string sinkName)
    {
        using JsonDocument document = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>(
            $"/receipts/{sinkName}") ?? throw new InvalidOperationException("MockSink returned no receipt document.");
        return document.RootElement.GetProperty("count").GetInt32();
    }

    private async Task AssertReceiptHeadersAsync(string sinkName, IReadOnlyDictionary<string, string> headers)
    {
        using HttpResponseMessage response = await fixture.MockSinkClient.PostAsJsonAsync(
            $"/receipts/{sinkName}/assert-headers",
            new { headers });
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Header assertion failed: {body}");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        Exception? lastException = null;
        while (deadline.Elapsed < (timeout ?? EvidenceTimeout))
        {
            try
            {
                if (await condition())
                    return;
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Resilience evidence was not ready within {timeout ?? EvidenceTimeout}. {lastException?.Message}");
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private sealed record Pipeline(
        Guid TenantId,
        string TenantSlug,
        string ApiToken,
        Guid SourceConnectionId,
        string TopicName,
        string EventType,
        Guid SubscriptionId,
        string SinkName);
}
