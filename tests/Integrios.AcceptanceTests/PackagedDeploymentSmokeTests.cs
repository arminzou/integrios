using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Integrios.AcceptanceTests;

[Collection(PackagedDeploymentCollection.Name)]
public sealed class PackagedDeploymentSmokeTests(PackagedDeploymentFixture fixture)
{
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task PackagedDeployment_StartsAndExposesDeterministicEvidence()
    {
        await AssertHealthyAsync(fixture.AdminClient);
        await AssertHealthyAsync(fixture.IngestionClient);
        await AssertHealthyAsync(fixture.MockSinkClient);

        (await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM connectors WHERE key = 'http' AND status = 'active'")).ShouldBe(1L);
        (await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(1L);

        const string sinkName = "acceptance-harness";
        const string headerValue = "expected-value";
        const string body = "{\"event\":\"packaged-deployment\"}";

        using HttpResponseMessage resetBefore = await fixture.MockSinkClient.DeleteAsync($"/receipts/{sinkName}");
        resetBefore.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var delivery = new HttpRequestMessage(HttpMethod.Post, $"/sink/{sinkName}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        delivery.Headers.Add("X-Acceptance", headerValue);
        using HttpResponseMessage delivered = await fixture.MockSinkClient.SendAsync(delivery);
        delivered.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument receiptQuery = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>(
            $"/receipts/{sinkName}") ?? throw new InvalidOperationException("MockSink returned no receipt document.");
        JsonElement root = receiptQuery.RootElement;
        root.GetProperty("count").GetInt32().ShouldBe(1);
        JsonElement receipt = root.GetProperty("receipts")[0];
        receipt.GetProperty("method").GetString().ShouldBe("POST");
        receipt.GetProperty("path").GetString().ShouldBe($"/sink/{sinkName}");
        receipt.GetProperty("body").GetString().ShouldBe(body);
        receipt.GetProperty("headerNames").EnumerateArray().Select(value => value.GetString()).ShouldContain(
            "X-Acceptance");

        using HttpResponseMessage headerAssertion = await fixture.MockSinkClient.PostAsJsonAsync(
            $"/receipts/{sinkName}/assert-headers",
            new { headers = new Dictionary<string, string> { ["X-Acceptance"] = headerValue } });
        string assertionEvidence = await headerAssertion.Content.ReadAsStringAsync();
        headerAssertion.StatusCode.ShouldBe(HttpStatusCode.OK);
        assertionEvidence.ShouldContain("\"matched\":true", Case.Sensitive);
        assertionEvidence.ShouldNotContain(headerValue, Case.Sensitive);

        using HttpResponseMessage resetAfter = await fixture.MockSinkClient.DeleteAsync($"/receipts/{sinkName}");
        resetAfter.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument emptyQuery = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>(
            $"/receipts/{sinkName}") ?? throw new InvalidOperationException("MockSink returned no reset receipt document.");
        emptyQuery.RootElement.GetProperty("count").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task OperatorObservability_ExposesMetricsLogsAndContinuousRetryTrace()
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string tenantSlug = $"otel-{suffix}";
        string sinkName = $"otel-{suffix}";

        Guid tenantId = await PostAdminForIdAsync(
            "/admin/tenants",
            new { slug = tenantSlug, name = "OTLP acceptance", environment = "production" });
        string apiToken = await PostAdminForPropertyAsync(
            $"/admin/tenants/{tenantId}/tenant-api-keys",
            new { name = "acceptance-ingestion" },
            "token");

        Guid sourceConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = "00000000-0000-0000-0000-000000000001",
                name = "acceptance-source",
                config = new { base_uri = $"http://mocksink:8080/sink/{sinkName}-source" },
                environment = "production"
            });
        Guid destinationConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = "00000000-0000-0000-0000-000000000001",
                name = "acceptance-destination",
                config = new { base_uri = $"http://mocksink:8080/sink/{sinkName}" },
                environment = "production"
            });
        Guid topicId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics",
            new { name = $"payments-{suffix}" });
        Guid sourceId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/sources",
            new { connection_id = sourceConnectionId, topic_id = topicId, type = "event_api", configuration = new { source_contract = "event_json" } });
        Guid subscriptionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions",
            new
            {
                name = "acceptance-retry",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = destinationConnectionId
            });

        using HttpResponseMessage failMode = await fixture.MockSinkClient.PutAsJsonAsync(
            $"/control/{sinkName}",
            new { mode = "fail" });
        failMode.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var ingest = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={sourceId}")
        {
            Content = JsonContent.Create(new
            {
                event_type = "payment.created",
                source_event_id = $"acceptance-{suffix}",
                payload = new { paymentId = $"pay-{suffix}", amount = 1200 },
            })
        };
        ingest.Headers.TryAddWithoutValidation("Authorization", $"TenantApiKey {apiToken}");
        using HttpResponseMessage accepted = await fixture.IngestionClient.SendAsync(ingest);
        string acceptedBody = await accepted.Content.ReadAsStringAsync();
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using JsonDocument acceptedDocument = JsonDocument.Parse(acceptedBody);
        Guid eventId = acceptedDocument.RootElement.GetProperty("event_id").GetGuid();

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}' AND da.status = 'failed'") >= 1);

        using HttpResponseMessage successMode = await fixture.MockSinkClient.DeleteAsync($"/control/{sinkName}");
        successMode.StatusCode.ShouldBe(HttpStatusCode.OK);

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM event_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'") == "succeeded");
        ((await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}'")) >= 2).ShouldBeTrue();

        Guid deliveryId = await fixture.ScalarAsync<Guid>(
            $"SELECT id FROM event_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'");
        string traceparent = await fixture.ScalarAsync<string>(
            $"SELECT traceparent FROM event_deliveries WHERE id = '{deliveryId}'");
        string traceId = traceparent.Split('-')[1];

        IReadOnlyList<ExportedSpan> traceSpans = [];
        // Ingestion and Worker export through independent batch processors, so delivery spans can
        // reach the collector before the acceptance span the Ingestion emitted seconds earlier.
        // Wait for the whole causal chain, not just its tail, or the assertions below race the flush.
        await WaitForAsync(async () =>
        {
            traceSpans = ParseSpans(await fixture.ReadTraceArtifactsAsync())
                .Where(span => span.TraceId == traceId)
                .ToArray();
            return traceSpans.Any(span => span.Name == "IngestEventCommand")
                && traceSpans.Any(span => span.Name == "outbox.fanout")
                && traceSpans.Count(span => span.Name == "subscription.deliver") >= 2;
        });

        ExportedSpan acceptanceSpan = traceSpans.Where(span => span.Name == "IngestEventCommand").ShouldHaveSingleItem();
        ExportedSpan fanoutSpan = traceSpans.Where(span => span.Name == "outbox.fanout").ShouldHaveSingleItem();
        ExportedSpan[] deliverySpans = traceSpans.Where(span => span.Name == "subscription.deliver").ToArray();
        fanoutSpan.ParentSpanId.ShouldBe(acceptanceSpan.SpanId);
        foreach (var span in deliverySpans)
            span.ParentSpanId.ShouldBe(fanoutSpan.SpanId);

        string ingestionMetrics = await fixture.IngestionClient.GetStringAsync("/metrics");
        string adminMetrics = await fixture.AdminClient.GetStringAsync("/metrics");
        string workerMetrics = await fixture.WorkerMetricsClient.GetStringAsync("/metrics");
        ingestionMetrics.ShouldContain("integrios_events_ingested_total", Case.Sensitive);
        adminMetrics.ShouldContain("http_server_request_duration", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_fanout_rows_created_total", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_deliveries_failed_total", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_deliveries_succeeded_total", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_delivery_attempt_duration_seconds", Case.Sensitive);
        ingestionMetrics.ShouldNotContain("integrios_outbox_pending_depth", Case.Sensitive);
        adminMetrics.ShouldNotContain("integrios_outbox_pending_depth", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_outbox_pending_depth", Case.Sensitive);

        string workerLogs = await fixture.GetServiceLogsAsync("worker");
        workerLogs.ShouldContain(eventId.ToString(), Case.Sensitive);
        workerLogs.ShouldContain(deliveryId.ToString(), Case.Sensitive);
        workerLogs.ShouldContain(subscriptionId.ToString(), Case.Sensitive);
        workerLogs.ShouldNotContain("acceptance-admin-secret", Case.Sensitive);
        workerLogs.ShouldNotContain(apiToken, Case.Sensitive);
    }

    [Fact]
    public async Task Worker_FansOutNewEventWhileDeliveryAttemptIsBlocked()
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string tenantSlug = $"loop-{suffix}";
        string topicName = $"loop-{suffix}";
        string sinkName = $"loop-{suffix}";

        Guid tenantId = await PostAdminForIdAsync(
            "/admin/tenants",
            new { slug = tenantSlug, name = "Worker loop isolation", environment = "production" });
        string apiToken = await PostAdminForPropertyAsync(
            $"/admin/tenants/{tenantId}/tenant-api-keys",
            new { name = "loop-isolation-ingestion" },
            "token");
        Guid sourceConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = "00000000-0000-0000-0000-000000000001",
                name = "loop-isolation-source",
                config = new { base_uri = $"http://mocksink:8080/sink/{sinkName}-source" },
                environment = "production"
            });
        Guid destinationConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = "00000000-0000-0000-0000-000000000001",
                name = "loop-isolation-destination",
                config = new { base_uri = $"http://mocksink:8080/sink/{sinkName}" },
                environment = "production"
            });
        Guid topicId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics",
            new { name = topicName });
        Guid sourceId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/sources",
            new { connection_id = sourceConnectionId, topic_id = topicId, type = "event_api", configuration = new { source_contract = "event_json" } });
        Guid subscriptionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions",
            new
            {
                name = "blocked-delivery",
                match_rules = new { event_type = "delivery.blocked" },
                destination_connection_id = destinationConnectionId
            });

        using HttpResponseMessage slowMode = await fixture.MockSinkClient.PutAsJsonAsync(
            $"/control/{sinkName}",
            new { mode = "slow", delayMs = 8000 });
        slowMode.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid? blockedEventId = null;

        try
        {
            blockedEventId = await IngestEventAsync(
                apiToken,
                sourceId,
                "delivery.blocked",
                $"blocked-{suffix}");
            await WaitForAsync(async () =>
                await fixture.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{blockedEventId.Value}' AND da.status = 'in_progress'") == 1);
            Guid blockedAttemptId = await fixture.ScalarAsync<Guid>(
                $"SELECT da.id FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{blockedEventId.Value}' AND da.status = 'in_progress'");

            Guid independentEventId = await IngestEventAsync(
                apiToken,
                sourceId,
                "fanout.independent",
                $"independent-{suffix}");

            await WaitForAsync(async () =>
                await fixture.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM events e JOIN outbox o ON o.event_id = e.id WHERE e.id = '{independentEventId}' AND e.status = 'unrouted' AND o.processed_at IS NOT NULL AND EXISTS (SELECT 1 FROM delivery_attempts WHERE id = '{blockedAttemptId}' AND status = 'in_progress')") == 1);
        }
        finally
        {
            using HttpResponseMessage reset = await fixture.MockSinkClient.DeleteAsync($"/control/{sinkName}");
            reset.StatusCode.ShouldBe(HttpStatusCode.OK);
            if (blockedEventId is { } eventId)
            {
                await WaitForAsync(async () =>
                    await fixture.ScalarAsync<long>(
                        $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}' AND da.status = 'in_progress'") == 0);
            }
        }

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM event_deliveries WHERE subscription_id = '{subscriptionId}' ORDER BY created_at DESC LIMIT 1") == "succeeded");
    }

    private async Task<Guid> PostAdminForIdAsync(string path, object body) =>
        Guid.Parse(await PostAdminForPropertyAsync(path, body, "id"));

    private async Task<string> PostAdminForPropertyAsync(string path, object body, string property)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        using HttpResponseMessage response = await fixture.AdminClient.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"POST {path} returned {(int)response.StatusCode}: {responseBody}");
        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document.RootElement.GetProperty(property).ToString();
    }

    private async Task<Guid> IngestEventAsync(
        string apiToken,
        Guid sourceId,
        string eventType,
        string sourceEventId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={sourceId}")
        {
            Content = JsonContent.Create(new
            {
                event_type = eventType,
                source_event_id = sourceEventId,
                payload = new { sourceEventId },
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"TenantApiKey {apiToken}");
        using HttpResponseMessage response = await fixture.IngestionClient.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document.RootElement.GetProperty("event_id").GetGuid();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        Exception? lastException = null;
        while (deadline.Elapsed < EvidenceTimeout)
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

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException($"Observability evidence was not ready within {EvidenceTimeout}. {lastException?.Message}");
    }

    private static IReadOnlyList<ExportedSpan> ParseSpans(string jsonLines)
    {
        var spans = new List<ExportedSpan>();
        foreach (string line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            Visit(root, spans);
        }

        return spans;
    }

    private static void Visit(JsonNode? node, ICollection<ExportedSpan> spans)
    {
        if (node is JsonObject obj)
        {
            if (obj["name"] is JsonValue name
                && obj["traceId"] is JsonValue traceId
                && obj["spanId"] is JsonValue spanId)
            {
                spans.Add(new ExportedSpan(
                    name.GetValue<string>(),
                    traceId.GetValue<string>(),
                    spanId.GetValue<string>(),
                    obj["parentSpanId"]?.GetValue<string>() ?? string.Empty));
            }

            foreach ((_, JsonNode? child) in obj)
                Visit(child, spans);
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                Visit(child, spans);
        }
    }

    private sealed record ExportedSpan(string Name, string TraceId, string SpanId, string ParentSpanId);

    private static async Task AssertHealthyAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
