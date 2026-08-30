using System.Diagnostics;
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
        await AssertHealthyAsync(fixture.AdminOperationalClient);
        await AssertHealthyAsync(fixture.IngestionOperationalClient);
        await AssertHealthyAsync(fixture.WorkerOperationalClient);
        await fixture.WireMockSink.AssertHealthyAsync();

        (await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM connectors WHERE id = '{fixture.HttpConnectorId}' AND key = 'http' AND status = 'active'")).ShouldBe(1L);
        (await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM operator_keys WHERE revoked_at IS NULL")).ShouldBe(1L);

        const string sinkName = "acceptance-harness";
        const string headerValue = "expected-value";
        const string body = "{\"event\":\"packaged-deployment\"}";

        await fixture.WireMockSink.ResetReceiptsAsync(sinkName);
        await fixture.WireMockSink.PostAsync(
            sinkName,
            body,
            new Dictionary<string, string> { ["X-Acceptance"] = headerValue });
        (await fixture.WireMockSink.ReceiptCountAsync(sinkName)).ShouldBe(1);
        await fixture.WireMockSink.AssertReceiptAsync(sinkName, body, "X-Acceptance");
        await fixture.WireMockSink.AssertReceiptHeaderAsync(sinkName, "X-Acceptance", headerValue);
        await fixture.WireMockSink.ResetReceiptsAsync(sinkName);
        (await fixture.WireMockSink.ReceiptCountAsync(sinkName)).ShouldBe(0);
    }

    [Fact]
    public async Task OperationalEndpoints_ArePrivateAndReadinessTracksOnlyTheDatabase()
    {
        HttpClient[] operationalClients =
        [
            fixture.AdminOperationalClient,
            fixture.IngestionOperationalClient,
            fixture.WorkerOperationalClient
        ];

        foreach (HttpClient client in operationalClients)
        {
            (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        foreach (HttpClient publicClient in (HttpClient[])[fixture.AdminClient, fixture.IngestionClient])
        {
            (await publicClient.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await publicClient.GetAsync("/ready")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await publicClient.GetAsync("/metrics")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        await fixture.StopPostgresAsync();
        try
        {
            foreach (HttpClient client in operationalClients)
            {
                (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
                (await client.GetAsync("/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            }
        }
        finally
        {
            await fixture.StartPostgresAsync();
            await fixture.RestartProductServicesAsync();
        }
    }

    [Fact]
    public async Task OperatorObservability_ExposesMetricsLogsAndContinuousRetryTrace()
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string tenantSlug = $"otel-{suffix}";
        string sinkName = $"otel-{suffix}";
        string sourceBaseUri = $"http://mocksink:8080/sink/{sinkName}-source";
        string destinationBaseUri = $"http://mocksink:8080/sink/{sinkName}";
        string payloadCanary = $"payload-secret-{suffix}";
        string sourceEventCanary = $"source-event-secret-{suffix}";
        string queryCanary = $"query-secret-{suffix}";
        string headerCanary = $"header-secret-{suffix}";

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
                connector_id = fixture.HttpConnectorId,
                name = "acceptance-source",
                config = new { base_uri = sourceBaseUri },
                environment = "production"
            });
        Guid destinationConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = fixture.HttpConnectorId,
                name = "acceptance-destination",
                config = new { base_uri = destinationBaseUri },
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

        // A refused connection is the only exercised path that leaves DeliveryResult.Error non-null
        // on the HTTP phase. Without it the exported-status assertion below is vacuous for
        // delivery.http, and a span that copied the transport message would still pass.
        Guid unreachableConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = fixture.HttpConnectorId,
                name = "acceptance-unreachable",
                config = new { base_uri = "http://mocksink:9/sink/unreachable" },
                environment = "production"
            });
        await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions",
            new
            {
                name = "acceptance-unreachable",
                match_rules = new { event_type = "payment.unreachable" },
                destination_connection_id = unreachableConnectionId
            });

        await fixture.WireMockSink.ConfigureAsync(sinkName, "fail");

        using var ingest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/events?source_id={sourceId}&canary={queryCanary}")
        {
            Content = JsonContent.Create(new
            {
                event_type = "payment.created",
                source_event_id = sourceEventCanary,
                payload = new { paymentId = payloadCanary, amount = 1200 },
            })
        };
        ingest.Headers.TryAddWithoutValidation("Authorization", $"TenantApiKey {apiToken}");
        ingest.Headers.TryAddWithoutValidation("X-Acceptance-Canary", headerCanary);
        Guid eventId;
        string ingestionRequestTraceId;
        using (var requestActivity = new Activity("acceptance.ingestion-request")
                   .SetIdFormat(ActivityIdFormat.W3C)
                   .Start())
        {
            ingestionRequestTraceId = requestActivity.TraceId.ToString();
            ingest.Headers.TryAddWithoutValidation("traceparent", requestActivity.Id);
            using HttpResponseMessage accepted = await fixture.IngestionClient.SendAsync(ingest);
            string acceptedBody = await accepted.Content.ReadAsStringAsync();
            accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            using JsonDocument acceptedDocument = JsonDocument.Parse(acceptedBody);
            eventId = acceptedDocument.RootElement.GetProperty("event_id").GetGuid();
        }

        Guid unreachableEventId = await IngestEventAsync(
            apiToken,
            sourceId,
            "payment.unreachable",
            $"unreachable-{suffix}");

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}' AND da.status = 'failed'") >= 1);

        await fixture.WireMockSink.ResetControlAsync(sinkName);

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
            traceSpans = ParseTraceArtifacts(await fixture.ReadTraceArtifactsAsync()).Spans
                .Where(span => span.TraceId == traceId)
                .ToArray();
            return traceSpans.Any(span => span.Name == "event.accept")
                && traceSpans.Any(span => span.Name == "outbox.fanout")
                && traceSpans.Count(span => span.Name == "delivery.attempt") >= 2
                && traceSpans.Count(span => span.Name == "delivery.transform") >= 2
                && traceSpans.Count(span => span.Name == "delivery.http") >= 2
                && traceSpans.Count(span => span.Name == "delivery.finalize") >= 2;
        });

        ExportedSpan acceptanceSpan = traceSpans.Where(span => span.Name == "event.accept").ShouldHaveSingleItem();
        ExportedSpan fanoutSpan = traceSpans.Where(span => span.Name == "outbox.fanout").ShouldHaveSingleItem();
        ExportedSpan[] deliverySpans = traceSpans.Where(span => span.Name == "delivery.attempt").ToArray();
        fanoutSpan.ParentSpanId.ShouldBe(acceptanceSpan.SpanId);
        foreach (var span in deliverySpans)
            span.ParentSpanId.ShouldBe(fanoutSpan.SpanId);
        traceSpans.ShouldNotContain(span => span.Name == "IngestEventCommand" || span.Name == "subscription.deliver");

        string ingestionMetrics = await fixture.IngestionOperationalClient.GetStringAsync("/metrics");
        string adminMetrics = await fixture.AdminOperationalClient.GetStringAsync("/metrics");
        string workerMetrics = await fixture.WorkerOperationalClient.GetStringAsync("/metrics");
        ingestionMetrics.ShouldContain("integrios_events_ingested_total", Case.Sensitive);
        adminMetrics.ShouldContain("http_server_request_duration", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_fanout_rows_created_total", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_deliveries_failed_total", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_deliveries_succeeded_total", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_delivery_attempt_duration_seconds", Case.Sensitive);
        ingestionMetrics.ShouldNotContain("integrios_outbox_pending_depth", Case.Sensitive);
        adminMetrics.ShouldNotContain("integrios_outbox_pending_depth", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_outbox_pending_depth", Case.Sensitive);

        // Four backlog gauges joined the existing pending-depth gauge and are Worker-only.
        workerMetrics.ShouldContain("integrios_outbox_oldest_pending_age_seconds", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_delivery_ready_depth", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_delivery_oldest_ready_age_seconds", Case.Sensitive);
        workerMetrics.ShouldContain("integrios_backlog_snapshot_age_seconds", Case.Sensitive);

        // The failed first attempt carries its explicit failure phase, and the phase span that
        // actually failed carries Error status.
        deliverySpans
            .Where(span => span.Attributes.ContainsKey("integrios.failure_phase"))
            .ShouldNotBeEmpty("No delivery.attempt span recorded a failure phase.");
        traceSpans
            .Where(span => span.Name == "delivery.http" && span.StatusCode == 2)
            .ShouldNotBeEmpty("No delivery.http span recorded Error status for the failed attempt.");
        deliverySpans.ShouldAllBe(span => span.Attributes.ContainsKey("integrios.event.id"));
        deliverySpans.ShouldAllBe(span => span.Attributes.ContainsKey("integrios.delivery.id"));
        deliverySpans.ShouldAllBe(span => span.Attributes.ContainsKey("integrios.attempt.number"));

        // The explicit always_on sampler keeps the sampled bit set across the durable
        // hop, so every span of the canary trace carries it -- Ingestion's and Worker's alike.
        traceSpans.ShouldAllBe(span => (span.Flags & 1) == 1);

        // Admin hands an Operator the same trace id the exported spans carry.
        using var recoveryRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/tenants/{tenantId}/events/{eventId}/deliveries");
        recoveryRequest.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        string recoveryRequestTraceId;
        using (var requestActivity = new Activity("acceptance.admin-request")
                   .SetIdFormat(ActivityIdFormat.W3C)
                   .Start())
        {
            recoveryRequestTraceId = requestActivity.TraceId.ToString();
            recoveryRequest.Headers.TryAddWithoutValidation("traceparent", requestActivity.Id);
            using HttpResponseMessage recovery = await fixture.AdminClient.SendAsync(recoveryRequest);
            recovery.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument recoveryDocument = JsonDocument.Parse(await recovery.Content.ReadAsStringAsync());
            recoveryDocument.RootElement.GetProperty("trace_id").GetString().ShouldBe(traceId);
        }

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{unreachableEventId}' AND da.status = 'failed' AND da.error_message IS NOT NULL") >= 1);
        string unreachableTraceparent = await fixture.ScalarAsync<string>(
            $"SELECT traceparent FROM event_deliveries WHERE event_id = '{unreachableEventId}'");
        string unreachableTraceId = unreachableTraceparent.Split('-')[1];

        string traceArtifacts = string.Empty;
        IReadOnlyList<ExportedSpan> allSpans = [];
        IReadOnlyList<IReadOnlyDictionary<string, string>> resources = [];
        string[] expectedServiceNames = ["integrios-admin", "integrios-ingestion", "integrios-worker"];
        await WaitForAsync(async () =>
        {
            traceArtifacts = await fixture.ReadTraceArtifactsAsync();
            (allSpans, resources) = ParseTraceArtifacts(traceArtifacts);
            return expectedServiceNames.All(serviceName =>
                resources.Any(resource =>
                    resource.TryGetValue("service.name", out string? actual) && actual == serviceName))
                && allSpans.Any(span =>
                    span.TraceId == unreachableTraceId && span.Name == "delivery.http");
        });

        allSpans
            .Where(span => span.TraceId == unreachableTraceId && span.Name == "delivery.http")
            .ShouldAllBe(span => span.StatusCode == 2);

        string[] testTraceIds = [traceId, unreachableTraceId, recoveryRequestTraceId];
        foreach (ExportedSpan span in allSpans.Where(span => testTraceIds.Contains(span.TraceId)))
        {
            span.StatusMessage.ShouldBeNullOrEmpty($"Span '{span.Name}' exported a status description.");
            foreach (string key in (string[])["http.route", "url.path", "http.target"])
            {
                if (span.Attributes.TryGetValue(key, out string? path))
                {
                    path.ShouldNotBe("/health");
                    path.ShouldNotBe("/ready");
                    path.ShouldNotBe("/metrics");
                }
            }
        }

        string expectedVersion = typeof(PackagedDeploymentSmokeTests).Assembly.GetName().Version!.ToString(3);
        foreach (string serviceName in expectedServiceNames)
        {
            IReadOnlyList<IReadOnlyDictionary<string, string>> serviceResources = resources
                .Where(resource => resource.TryGetValue("service.name", out string? actual) && actual == serviceName)
                .ToArray();
            serviceResources.ShouldNotBeEmpty();
            foreach (IReadOnlyDictionary<string, string> resource in serviceResources)
            {
                resource["service.version"].ShouldBe(expectedVersion);
                resource["service.instance.id"].ShouldNotBeNullOrWhiteSpace();
                resource["deployment.environment.name"].ShouldBe("acceptance");
            }
        }

        var serviceInstances = resources
            .Where(resource => expectedServiceNames.Contains(resource.GetValueOrDefault("service.name")))
            .GroupBy(resource => resource["service.name"], StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(resource => resource["service.instance.id"]).Distinct(StringComparer.Ordinal));
        serviceInstances.SelectMany(pair => pair.Value.Select(instanceId => (pair.Key, instanceId)))
            .GroupBy(instance => instance.instanceId, StringComparer.Ordinal)
            .ShouldAllBe(group => group.Select(instance => instance.Key).Distinct(StringComparer.Ordinal).Count() == 1);

        string workerLogs = await fixture.GetServiceLogsAsync("worker");
        workerLogs.ShouldContain(eventId.ToString(), Case.Sensitive);
        workerLogs.ShouldContain(deliveryId.ToString(), Case.Sensitive);
        workerLogs.ShouldContain(subscriptionId.ToString(), Case.Sensitive);

        string ingestionLogs = await fixture.GetServiceLogsAsync("ingestion");
        string adminLogs = await fixture.GetServiceLogsAsync("admin");
        IReadOnlyList<JsonObject> workerRecords = ParseJsonLogRecords(workerLogs);
        IReadOnlyList<JsonObject> ingestionRecords = ParseJsonLogRecords(ingestionLogs);
        IReadOnlyList<JsonObject> adminRecords = ParseJsonLogRecords(adminLogs);
        AssertJsonEnvelope("worker", workerRecords);
        AssertJsonEnvelope("ingestion", ingestionRecords);
        AssertJsonEnvelope("admin", adminRecords);

        AssertSingleCompletion(
            ingestionRecords,
            "POST",
            "/events",
            (int)HttpStatusCode.Accepted,
            ingestionRequestTraceId);
        AssertSingleCompletion(
            adminRecords,
            "GET",
            "/admin/tenants/{tenantId:guid}/events/{eventId:guid}/deliveries",
            (int)HttpStatusCode.OK,
            recoveryRequestTraceId);
        workerRecords
            .Where(record => record["EventId"]?.GetValue<int>() == 1000)
            .ShouldBeEmpty();
        foreach (JsonObject completion in ingestionRecords.Concat(adminRecords)
                     .Where(record => record["EventId"]?.GetValue<int>() == 1000))
        {
            string route = completion["State"]?["route_template"]?.GetValue<string>() ?? string.Empty;
            route.ShouldNotBe("/health");
            route.ShouldNotBe("/ready");
            route.ShouldNotBe("/metrics");
        }

        foreach (string canary in (string[])
                 [
                     "acceptance-admin-secret",
                     apiToken,
                     payloadCanary,
                     sourceEventCanary,
                     queryCanary,
                     headerCanary,
                     sourceBaseUri,
                     destinationBaseUri
                 ])
        {
            workerLogs.ShouldNotContain(canary, Case.Sensitive, "Worker logs disclosed a canary value.");
            ingestionLogs.ShouldNotContain(canary, Case.Sensitive, "Ingestion logs disclosed a canary value.");
            adminLogs.ShouldNotContain(canary, Case.Sensitive, "Admin logs disclosed a canary value.");
            traceArtifacts.ShouldNotContain(canary, Case.Sensitive, "Exported traces disclosed a canary value.");
        }

        // Trace export is best-effort equipment: with the collector gone Delivery must continue.
        await fixture.StopCollectorAsync();
        try
        {
            await fixture.WireMockSink.ConfigureAsync(sinkName, "fail");
            await fixture.WireMockSink.ResetReceiptsAsync(sinkName);
            using var second = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={sourceId}")
            {
                Content = JsonContent.Create(new
                {
                    event_type = "payment.created",
                    source_event_id = $"acceptance-{suffix}-collectorless",
                    payload = new { paymentId = $"pay-{suffix}-2", amount = 1300 },
                })
            };
            second.Headers.TryAddWithoutValidation("Authorization", $"TenantApiKey {apiToken}");
            using HttpResponseMessage secondAccepted = await fixture.IngestionClient.SendAsync(second);
            secondAccepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            using JsonDocument secondDocument = JsonDocument.Parse(await secondAccepted.Content.ReadAsStringAsync());
            Guid secondEventId = secondDocument.RootElement.GetProperty("event_id").GetGuid();

            await WaitForAsync(async () =>
                await fixture.ScalarAsync<long>(
                    $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{secondEventId}' AND da.status = 'failed'") >= 1);

            await fixture.WireMockSink.ResetControlAsync(sinkName);

            await WaitForAsync(async () =>
                await fixture.ScalarAsync<string>(
                    $"SELECT status FROM event_deliveries WHERE event_id = '{secondEventId}' AND subscription_id = '{subscriptionId}'") == "succeeded");
            ((await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{secondEventId}'")) >= 2).ShouldBeTrue();
        }
        finally
        {
            await fixture.WireMockSink.ResetControlAsync(sinkName);
        }

        // Export failures during the outage must not wedge the pipeline: once the collector is
        // back, an Event ingested after it exports its whole chain without any intervention.
        await fixture.StartCollectorAsync();
        Guid recoveredEventId = await IngestEventAsync(
            apiToken,
            sourceId,
            "payment.created",
            $"recovered-{suffix}");
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM event_deliveries WHERE event_id = '{recoveredEventId}' AND subscription_id = '{subscriptionId}'") == "succeeded");
        string recoveredTraceparent = await fixture.ScalarAsync<string>(
            $"SELECT traceparent FROM event_deliveries WHERE event_id = '{recoveredEventId}' AND subscription_id = '{subscriptionId}'");
        string recoveredTraceId = recoveredTraceparent.Split('-')[1];

        await WaitForAsync(async () =>
        {
            ExportedSpan[] recoveredSpans = ParseTraceArtifacts(await fixture.ReadTraceArtifactsAsync()).Spans
                .Where(span => span.TraceId == recoveredTraceId)
                .ToArray();
            return recoveredSpans.Any(span => span.Name == "event.accept")
                && recoveredSpans.Any(span => span.Name == "outbox.fanout")
                && recoveredSpans.Any(span => span.Name == "delivery.attempt")
                && recoveredSpans.Any(span => span.Name == "delivery.finalize");
        });
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
                connector_id = fixture.HttpConnectorId,
                name = "loop-isolation-source",
                config = new { base_uri = $"http://mocksink:8080/sink/{sinkName}-source" },
                environment = "production"
            });
        Guid destinationConnectionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/connections",
            new
            {
                connector_id = fixture.HttpConnectorId,
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

        await fixture.WireMockSink.ConfigureAsync(sinkName, "slow", delayMs: 8000);
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
            await fixture.WireMockSink.ResetControlAsync(sinkName);
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

    private static (
        IReadOnlyList<ExportedSpan> Spans,
        IReadOnlyList<IReadOnlyDictionary<string, string>> Resources) ParseTraceArtifacts(string jsonLines)
    {
        var spans = new List<ExportedSpan>();
        var resources = new List<IReadOnlyDictionary<string, string>>();
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

            VisitTraceArtifacts(root, spans, resources);
        }

        return (spans, resources);
    }

    private static void VisitTraceArtifacts(
        JsonNode? node,
        ICollection<ExportedSpan> spans,
        ICollection<IReadOnlyDictionary<string, string>> resources)
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
                    obj["parentSpanId"]?.GetValue<string>() ?? string.Empty,
                    ReadAttributes(obj["attributes"]),
                    obj["status"]?["code"]?.GetValue<int>() ?? 0,
                    obj["status"]?["message"]?.GetValue<string>(),
                    obj["flags"]?.GetValue<int>() ?? 0));
            }

            if (obj["resource"] is JsonObject resource)
                resources.Add(ReadAttributes(resource["attributes"]));

            foreach ((_, JsonNode? child) in obj)
                VisitTraceArtifacts(child, spans, resources);
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                VisitTraceArtifacts(child, spans, resources);
        }
    }

    // OTLP JSON carries attributes as [{"key":..,"value":{"stringValue":..}}]; the concrete value
    // type varies per attribute, so the single populated member is flattened to its text.
    private static IReadOnlyDictionary<string, string> ReadAttributes(JsonNode? attributes)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (attributes is not JsonArray array)
            return values;

        foreach (JsonNode? entry in array)
        {
            if (entry?["key"]?.GetValue<string>() is not string key || entry["value"] is not JsonObject value)
                continue;

            foreach ((_, JsonNode? typed) in value)
            {
                if (typed is null)
                    continue;
                values[key] = typed.ToJsonString().Trim('"');
                break;
            }
        }

        return values;
    }

    // `docker compose logs` prefixes every line with "<service>  | "; every server-process record
    // after that prefix is part of the packaged JSON contract.
    private static IReadOnlyList<JsonObject> ParseJsonLogRecords(string composeLogs)
    {
        var records = new List<JsonObject>();
        foreach (string rawLine in composeLogs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = rawLine.IndexOf('|');
            string line = separator >= 0 ? rawLine[(separator + 1)..].Trim() : rawLine;
            if (JsonNode.Parse(line) is not JsonObject record)
                throw new JsonException("Server log record was not a JSON object.");
            records.Add(record);
        }

        return records;
    }

    private static void AssertJsonEnvelope(string serviceName, IReadOnlyList<JsonObject> records)
    {
        records.ShouldNotBeEmpty($"{serviceName} emitted no JSON log records.");

        foreach (JsonObject record in records)
        {
            foreach (string field in (string[])["Timestamp", "LogLevel", "Category", "Message"])
                record[field]?.GetValue<string>()
                    .ShouldNotBeNullOrWhiteSpace($"{serviceName} emitted a record without required {field}.");

            record["Timestamp"]!.GetValue<string>()
                .ShouldEndWith("Z", Case.Sensitive, $"{serviceName} emitted a non-UTC timestamp.");
        }
    }

    private static void AssertSingleCompletion(
        IReadOnlyList<JsonObject> records,
        string method,
        string route,
        int status,
        string traceId)
    {
        JsonObject record = records
            .Where(candidate =>
                candidate["EventId"]?.GetValue<int>() == 1000
                && FindActivityScope(candidate, traceId) is not null
                && string.Equals(
                    candidate["State"]?["route_template"]?.GetValue<string>()?.TrimEnd('/'),
                    route.TrimEnd('/'),
                    StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        JsonObject state = record["State"] as JsonObject
            ?? throw new JsonException("Completion record had no structured state.");

        state["method"]?.GetValue<string>().ShouldBe(method);
        state["status"]?.GetValue<int>().ShouldBe(status);
        state["duration_ms"]?.GetValue<double>().ShouldBeGreaterThanOrEqualTo(0);
        state["request_id"]?.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        FindActivityScope(record, traceId)!["SpanId"]?.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    private static JsonObject? FindActivityScope(JsonObject record, string traceId) =>
        record["Scopes"] is JsonArray scopes
            ? scopes.OfType<JsonObject>()
                .SingleOrDefault(scope => scope["TraceId"]?.GetValue<string>() == traceId)
            : null;

    private sealed record ExportedSpan(
        string Name,
        string TraceId,
        string SpanId,
        string ParentSpanId,
        IReadOnlyDictionary<string, string> Attributes,
        int StatusCode,
        string? StatusMessage,
        int Flags);

    private static async Task AssertHealthyAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
