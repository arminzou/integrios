using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Integrios.QualificationTests;

[Collection(PackagedDeploymentCollection.Name)]
[Trait("Category", "Qualification")]
public sealed class PackagedDeploymentSmokeTests(PackagedDeploymentFixture fixture)
{
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task PackagedDeployment_StartsAndExposesDeterministicEvidence()
    {
        await AssertHealthyAsync(fixture.AdminClient);
        await AssertHealthyAsync(fixture.IngressClient);
        await AssertHealthyAsync(fixture.MockSinkClient);

        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM integrations WHERE key = 'webhook' AND status = 'active'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM admin_keys WHERE revoked_at IS NULL"));

        const string sinkName = "qualification-harness";
        const string headerValue = "expected-value";
        const string body = "{\"event\":\"packaged-deployment\"}";

        using HttpResponseMessage resetBefore = await fixture.MockSinkClient.DeleteAsync($"/receipts/{sinkName}");
        Assert.Equal(HttpStatusCode.OK, resetBefore.StatusCode);

        using var delivery = new HttpRequestMessage(HttpMethod.Post, $"/sink/{sinkName}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        delivery.Headers.Add("X-Qualification", headerValue);
        using HttpResponseMessage delivered = await fixture.MockSinkClient.SendAsync(delivery);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        using JsonDocument receiptQuery = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>(
            $"/receipts/{sinkName}") ?? throw new InvalidOperationException("MockSink returned no receipt document.");
        JsonElement root = receiptQuery.RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        JsonElement receipt = root.GetProperty("receipts")[0];
        Assert.Equal("POST", receipt.GetProperty("method").GetString());
        Assert.Equal($"/sink/{sinkName}", receipt.GetProperty("path").GetString());
        Assert.Equal(body, receipt.GetProperty("body").GetString());
        Assert.Contains(
            "X-Qualification",
            receipt.GetProperty("headerNames").EnumerateArray().Select(value => value.GetString()));

        using HttpResponseMessage headerAssertion = await fixture.MockSinkClient.PostAsJsonAsync(
            $"/receipts/{sinkName}/assert-headers",
            new { headers = new Dictionary<string, string> { ["X-Qualification"] = headerValue } });
        string assertionEvidence = await headerAssertion.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, headerAssertion.StatusCode);
        Assert.Contains("\"matched\":true", assertionEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(headerValue, assertionEvidence, StringComparison.Ordinal);

        using HttpResponseMessage resetAfter = await fixture.MockSinkClient.DeleteAsync($"/receipts/{sinkName}");
        Assert.Equal(HttpStatusCode.OK, resetAfter.StatusCode);
        using JsonDocument emptyQuery = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>(
            $"/receipts/{sinkName}") ?? throw new InvalidOperationException("MockSink returned no reset receipt document.");
        Assert.Equal(0, emptyQuery.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task OperatorObservability_ExposesMetricsLogsAndContinuousRetryTrace()
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string tenantSlug = $"otel-{suffix}";
        string sinkName = $"otel-{suffix}";

        Guid tenantId = await PostAdminForIdAsync(
            "/admin/tenants",
            new { slug = tenantSlug, name = "OTLP qualification", environment = "production" });
        string apiToken = await PostAdminForPropertyAsync(
            $"/admin/tenants/{tenantId}/api-keys",
            new { name = "qualification-ingress" },
            "token");

        Guid sourceConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                integrationId = "00000000-0000-0000-0000-000000000001",
                name = "qualification-source",
                config = new { url = $"http://mocksink:8080/sink/{sinkName}-source" },
                environment = "production"
            });
        Guid destinationConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                integrationId = "00000000-0000-0000-0000-000000000001",
                name = "qualification-destination",
                config = new { url = $"http://mocksink:8080/sink/{sinkName}" },
                environment = "production"
            });
        Guid topicId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics",
            new
            {
                name = $"payments-{suffix}",
                sourceConnectionIds = new[] { sourceConnectionId }
            });
        Guid subscriptionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions",
            new
            {
                name = "qualification-retry",
                matchRules = new { event_type = "payment.created" },
                destinationConnectionId
            });

        using HttpResponseMessage failMode = await fixture.MockSinkClient.PutAsJsonAsync(
            $"/control/{sinkName}",
            new { mode = "fail" });
        Assert.Equal(HttpStatusCode.OK, failMode.StatusCode);

        using var ingest = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(new
            {
                sourceConnectionId,
                topicName = $"payments-{suffix}",
                eventType = "payment.created",
                payload = new { paymentId = $"pay-{suffix}", amount = 1200 },
                idempotencyKey = $"qualification-{suffix}"
            })
        };
        ingest.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {apiToken}");
        using HttpResponseMessage accepted = await fixture.IngressClient.SendAsync(ingest);
        string acceptedBody = await accepted.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using JsonDocument acceptedDocument = JsonDocument.Parse(acceptedBody);
        Guid eventId = acceptedDocument.RootElement.GetProperty("eventId").GetGuid();

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{eventId}' AND da.status = 'failed'") >= 1);

        using HttpResponseMessage successMode = await fixture.MockSinkClient.DeleteAsync($"/control/{sinkName}");
        Assert.Equal(HttpStatusCode.OK, successMode.StatusCode);

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM subscription_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'") == "succeeded");
        Assert.True(await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{eventId}'") >= 2);

        Guid deliveryId = await fixture.ScalarAsync<Guid>(
            $"SELECT id FROM subscription_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'");
        string traceparent = await fixture.ScalarAsync<string>(
            $"SELECT traceparent FROM subscription_deliveries WHERE id = '{deliveryId}'");
        string traceId = traceparent.Split('-')[1];

        IReadOnlyList<ExportedSpan> traceSpans = [];
        // Ingress and Worker export through independent batch processors, so delivery spans can
        // reach the collector before the acceptance span the Ingress emitted seconds earlier.
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

        ExportedSpan acceptanceSpan = Assert.Single(traceSpans, span => span.Name == "IngestEventCommand");
        ExportedSpan fanoutSpan = Assert.Single(traceSpans, span => span.Name == "outbox.fanout");
        ExportedSpan[] deliverySpans = traceSpans.Where(span => span.Name == "subscription.deliver").ToArray();
        Assert.Equal(acceptanceSpan.SpanId, fanoutSpan.ParentSpanId);
        Assert.All(deliverySpans, span => Assert.Equal(fanoutSpan.SpanId, span.ParentSpanId));

        string ingressMetrics = await fixture.IngressClient.GetStringAsync("/metrics");
        string adminMetrics = await fixture.AdminClient.GetStringAsync("/metrics");
        string workerMetrics = await fixture.WorkerMetricsClient.GetStringAsync("/metrics");
        Assert.Contains("integrios_events_ingested_total", ingressMetrics, StringComparison.Ordinal);
        Assert.Contains("http_server_request_duration", adminMetrics, StringComparison.Ordinal);
        Assert.Contains("integrios_fanout_rows_created_total", workerMetrics, StringComparison.Ordinal);
        Assert.Contains("integrios_deliveries_failed_total", workerMetrics, StringComparison.Ordinal);
        Assert.Contains("integrios_deliveries_succeeded_total", workerMetrics, StringComparison.Ordinal);
        Assert.Contains("integrios_delivery_attempt_duration_seconds", workerMetrics, StringComparison.Ordinal);
        Assert.DoesNotContain("integrios_outbox_pending_depth", ingressMetrics, StringComparison.Ordinal);
        Assert.DoesNotContain("integrios_outbox_pending_depth", adminMetrics, StringComparison.Ordinal);
        Assert.Contains("integrios_outbox_pending_depth", workerMetrics, StringComparison.Ordinal);

        string workerLogs = await fixture.GetServiceLogsAsync("worker");
        Assert.Contains(eventId.ToString(), workerLogs, StringComparison.Ordinal);
        Assert.Contains(deliveryId.ToString(), workerLogs, StringComparison.Ordinal);
        Assert.Contains(subscriptionId.ToString(), workerLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("qualification-admin-secret", workerLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(apiToken, workerLogs, StringComparison.Ordinal);
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
            $"/admin/tenants/{tenantId}/api-keys",
            new { name = "loop-isolation-ingress" },
            "token");
        Guid sourceConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                integrationId = "00000000-0000-0000-0000-000000000001",
                name = "loop-isolation-source",
                config = new { url = $"http://mocksink:8080/sink/{sinkName}-source" },
                environment = "production"
            });
        Guid destinationConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                integrationId = "00000000-0000-0000-0000-000000000001",
                name = "loop-isolation-destination",
                config = new { url = $"http://mocksink:8080/sink/{sinkName}" },
                environment = "production"
            });
        Guid topicId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics",
            new { name = topicName, sourceConnectionIds = new[] { sourceConnectionId } });
        Guid subscriptionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions",
            new
            {
                name = "blocked-delivery",
                matchRules = new { event_type = "delivery.blocked" },
                destinationConnectionId
            });

        using HttpResponseMessage slowMode = await fixture.MockSinkClient.PutAsJsonAsync(
            $"/control/{sinkName}",
            new { mode = "slow", delayMs = 8000 });
        Assert.Equal(HttpStatusCode.OK, slowMode.StatusCode);
        Guid? blockedEventId = null;

        try
        {
            blockedEventId = await IngestEventAsync(
                apiToken,
                sourceConnectionId,
                topicName,
                "delivery.blocked",
                $"blocked-{suffix}");
            await WaitForAsync(async () =>
                await fixture.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{blockedEventId.Value}' AND da.status = 'in_progress'") == 1);
            Guid blockedAttemptId = await fixture.ScalarAsync<Guid>(
                $"SELECT da.id FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{blockedEventId.Value}' AND da.status = 'in_progress'");

            Guid independentEventId = await IngestEventAsync(
                apiToken,
                sourceConnectionId,
                topicName,
                "fanout.independent",
                $"independent-{suffix}");

            await WaitForAsync(async () =>
                await fixture.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM events e JOIN outbox o ON o.event_id = e.id WHERE e.id = '{independentEventId}' AND e.status = 'unrouted' AND o.processed_at IS NOT NULL AND EXISTS (SELECT 1 FROM delivery_attempts WHERE id = '{blockedAttemptId}' AND status = 'in_progress')") == 1);
        }
        finally
        {
            using HttpResponseMessage reset = await fixture.MockSinkClient.DeleteAsync($"/control/{sinkName}");
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            if (blockedEventId is { } eventId)
            {
                await WaitForAsync(async () =>
                    await fixture.ScalarAsync<long>(
                        $"SELECT COUNT(*) FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{eventId}' AND da.status = 'in_progress'") == 0);
            }
        }

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM subscription_deliveries WHERE subscription_id = '{subscriptionId}' ORDER BY created_at DESC LIMIT 1") == "succeeded");
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

    private async Task<Guid> IngestEventAsync(
        string apiToken,
        Guid sourceConnectionId,
        string topicName,
        string eventType,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(new
            {
                sourceConnectionId,
                topicName,
                eventType,
                payload = new { idempotencyKey },
                idempotencyKey
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {apiToken}");
        using HttpResponseMessage response = await fixture.IngressClient.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document.RootElement.GetProperty("eventId").GetGuid();
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
