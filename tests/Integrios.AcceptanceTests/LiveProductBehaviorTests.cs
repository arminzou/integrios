using Integrios.Tests.Shared;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Integrios.AcceptanceTests;

[Collection(PackagedDeploymentCollection.Name)]
public sealed class LiveProductBehaviorTests(PackagedDeploymentFixture fixture)
{
    private const string HttpConnectorId = "00000000-0000-0000-0000-000000000001";
    private const string ApiKeyConnectorId = "11111111-1111-1111-1111-111111111111";
    private const string BearerConnectorId = "22222222-2222-2222-2222-222222222222";
    private const string SourceOnlyConnectorId = "33333333-3333-3333-3333-333333333333";
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task PackagedSystem_QualifiesLiveProductBehaviorMatrix()
    {
        await InstallAcceptanceConnectorsAsync();
        await AssertControlPlaneAuthorityAsync();

        TenantContext primary = await CreateTenantAsync($"matrix-{Suffix()}");
        TenantContext isolated = await CreateTenantAsync($"isolated-{Suffix()}");
        await AssertTenantAndApiKeyContractsAsync(primary, isolated);

        Guid sourceConnection = await CreateConnectionAsync(primary, HttpConnectorId, "source", "http://mocksink:8080/sink/source");
        Guid topic = await CreateTopicAsync(primary, "payments");
        Guid source = await CreateEventApiSourceAsync(primary, sourceConnection, topic);
        await AssertSourceConnectionAndTopicContractsAsync(primary, isolated, source, topic);

        await AssertUnroutedAndIdempotentAcceptanceAsync(primary, source);
        await AssertFanoutTransformsAndAuthenticatedDeliveryAsync(primary, source, topic);
        await AssertRetryDeadLetterReplayAndSnapshotsAsync(primary, source, topic);
        await AssertDestinationBoundaryAsync(primary, source, topic);
        await AssertDrainBeforeChangeAsync();
        await AssertSecretProvidersAsync();
        await AssertBootstrapRestartAndOperatorKeyRotationAsync();
        await AssertSecretsAbsentFromDurableEvidenceAsync();
    }

    private async Task AssertControlPlaneAuthorityAsync()
    {
        using HttpResponseMessage unauthenticated = await fixture.AdminClient.GetAsync("/admin/tenants");
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage authenticated = await SendAdminAsync(HttpMethod.Get, "/admin/tenants");
        authenticated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task AssertTenantAndApiKeyContractsAsync(TenantContext primary, TenantContext isolated)
    {
        using HttpResponseMessage invalidSlug = await PostAdminAsync(
            "/admin/tenants",
            new { slug = "Invalid_Slug", name = "Invalid", environment = "production" });
        invalidSlug.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        using HttpResponseMessage duplicateTenant = await PostAdminAsync(
            "/admin/tenants",
            new { slug = primary.Slug, name = "Duplicate", environment = "production" });
        duplicateTenant.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using HttpResponseMessage tenantApiKeys = await SendAdminAsync(
            HttpMethod.Get,
            $"/admin/tenants/{primary.Id}/tenant-api-keys");
        string tenantApiKeysBody = await tenantApiKeys.Content.ReadAsStringAsync();
        tenantApiKeys.StatusCode.ShouldBe(HttpStatusCode.OK);
        tenantApiKeysBody.ShouldNotContain("scope", Case.Insensitive);

        using HttpResponseMessage spareKeyResponse = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/tenant-api-keys",
            new { name = "revocation-check" });
        JsonElement spareKey = await AssertJsonAsync(spareKeyResponse, HttpStatusCode.Created);
        Guid spareKeyId = spareKey.GetProperty("tenant_api_key").GetProperty("id").GetGuid();
        var spareContext = new TenantContext(primary.Id, primary.Slug, spareKeyId, spareKey.GetProperty("token").GetString()!);
        using HttpResponseMessage revoke = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/tenant-api-keys/{spareKeyId}/revoke",
            new { });
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage revokedDataPlane = await SendIngestionAsync(
            spareContext, HttpMethod.Get, $"/events/{Guid.NewGuid()}");
        revokedDataPlane.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        Guid sourceConnection = await CreateConnectionAsync(primary, HttpConnectorId, "tenant-read-source", "http://mocksink:8080/sink/read-source");
        Guid topic = await CreateTopicAsync(primary, "tenant-reads");
        Guid source = await CreateEventApiSourceAsync(primary, sourceConnection, topic);
        EventAcceptance accepted = await IngestAsync(primary, source, "tenant-reads", "read.test", new { ok = true });

        using HttpResponseMessage ownRead = await SendIngestionAsync(primary, HttpMethod.Get, $"/events/{accepted.Id}");
        ownRead.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage otherRead = await SendIngestionAsync(isolated, HttpMethod.Get, $"/events/{accepted.Id}");
        otherRead.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage otherReplay = await SendAdminAsync(
            HttpMethod.Get,
            $"/admin/tenants/{isolated.Id}/events/{accepted.Id}/deliveries");
        otherReplay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task AssertSourceConnectionAndTopicContractsAsync(
        TenantContext primary,
        TenantContext isolated,
        Guid validSource,
        Guid topic)
    {
        using HttpResponseMessage duplicateTopic = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/topics",
            new { name = "payments" });
        duplicateTopic.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        Guid isolatedConnection = await CreateConnectionAsync(isolated, HttpConnectorId, "isolated-source", "http://mocksink:8080/sink/isolated-source");
        Guid isolatedTopic = await CreateTopicAsync(isolated, "isolated-topic");
        Guid isolatedSource = await CreateEventApiSourceAsync(isolated, isolatedConnection, isolatedTopic);
        await AssertAcceptanceRejectedAsync(primary, isolatedSource, "payments");

        Guid unassociated = Guid.NewGuid();
        await AssertAcceptanceRejectedAsync(primary, unassociated, "payments");

        Guid destinationOnly = await CreateConnectionAsync(
            primary,
            ApiKeyConnectorId,
            "destination-only-source",
            "http://mocksink:8080/sink/destination-only",
            ApiKeyAuth("shared_secret"));
        await AssertAcceptanceRejectedAsync(primary, destinationOnly, "payments");

        Guid inactive = await CreateConnectionAsync(primary, HttpConnectorId, "inactive-source", "http://mocksink:8080/sink/inactive");
        Guid inactiveSource = await CreateEventApiSourceAsync(primary, inactive, topic);
        using HttpResponseMessage deactivate = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/connections/{inactive}/deactivate",
            new { });
        deactivate.StatusCode.ShouldBe(HttpStatusCode.OK);
        await AssertAcceptanceRejectedAsync(primary, inactiveSource, "payments");

        (await fixture.ScalarAsync<string>($"SELECT name FROM topics WHERE id = '{topic}'")).ShouldBe("payments");
    }

    private async Task AssertUnroutedAndIdempotentAcceptanceAsync(TenantContext tenant, Guid source)
    {
        EventAcceptance first = await IngestAsync(
            tenant,
            source,
            "payments",
            "no.subscription",
            new { value = 1 },
            "duplicate-matrix");
        EventAcceptance duplicate = await IngestAsync(
            tenant,
            source,
            "payments",
            "no.subscription",
            new { value = 999 },
            "duplicate-matrix");
        duplicate.Id.ShouldBe(first.Id);
        first.AlreadyAccepted.ShouldBeFalse();
        duplicate.AlreadyAccepted.ShouldBeTrue();

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>($"SELECT status FROM events WHERE id = '{first.Id}'") == "unrouted");
        (await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM event_deliveries WHERE event_id = '{first.Id}'")).ShouldBe(0L);
    }

    private async Task AssertFanoutTransformsAndAuthenticatedDeliveryAsync(
        TenantContext tenant,
        Guid source,
        Guid topic)
    {
        await fixture.WriteSecretAsync(tenant.Slug, "api_key", "api-key-value");
        await fixture.WriteSecretAsync(tenant.Slug, "bearer_token", "bearer-token-value");

        Guid transformedDestination = await CreateConnectionAsync(
            tenant, HttpConnectorId, "transform-destination", "http://mocksink:8080/sink/transform");
        Guid apiDestination = await CreateConnectionAsync(
            tenant, ApiKeyConnectorId, "api-destination", "http://mocksink:8080/sink/api-auth", ApiKeyAuth("api_key"));
        Guid bearerDestination = await CreateConnectionAsync(
            tenant, BearerConnectorId, "bearer-destination", "http://mocksink:8080/sink/bearer-auth", BearerAuth("bearer_token"));

        object transform = Jsonata("{ \"kind\": $context.event_type, \"amount\": amount, \"topic\": $context.topic_name }");
        using HttpResponseMessage preview = await PostAdminAsync(
            "/admin/transform/preview",
            new
            {
                transform,
                sampleInput = new { amount = 42 },
                sampleContext = new { event_type = "payment.created", topic_name = "payments" }
            });
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage invalidTransform = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("invalid-transform", transformedDestination, "payment.created", Jsonata("{")));
        invalidTransform.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        string oversizedExpression = new('x', 65537);
        using HttpResponseMessage oversizedTransform = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("oversized-transform", transformedDestination, "payment.created", Jsonata(oversizedExpression)));
        oversizedTransform.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        Guid transformedSubscription = await CreateSubscriptionAsync(
            tenant, topic, "transformed", transformedDestination, "payment.created", transform);
        Guid apiSubscription = await CreateSubscriptionAsync(tenant, topic, "api-auth", apiDestination, "payment.created");
        Guid bearerSubscription = await CreateSubscriptionAsync(tenant, topic, "bearer-auth", bearerDestination, "payment.created");

        EventAcceptance accepted = await IngestAsync(
            tenant, source, "payments", "payment.created", new { amount = 42, paymentId = "pay-live" });
        await WaitForDeliveryStatusAsync(accepted.Id, transformedSubscription, "succeeded");
        await WaitForDeliveryStatusAsync(accepted.Id, apiSubscription, "succeeded");
        await WaitForDeliveryStatusAsync(accepted.Id, bearerSubscription, "succeeded");

        (await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM event_deliveries WHERE event_id = '{accepted.Id}'")).ShouldBe(3L);
        await AssertReceiptBodyAsync("transform", "{\"kind\":\"payment.created\",\"amount\":42,\"topic\":\"payments\"}");
        await AssertReceiptHeaderAsync("api-auth", "X-Api-Key", "api-key-value");
        await AssertReceiptHeaderAsync("bearer-auth", "Authorization", "Bearer bearer-token-value");

        string snapshots = await fixture.ScalarAsync<string>(
            $"SELECT string_agg(COALESCE((http_execution_snapshot->'destination_authentication')::text, 'open'), '|') FROM event_deliveries WHERE event_id = '{accepted.Id}'");
        snapshots.ShouldContain("api_key_header", Case.Sensitive);
        snapshots.ShouldContain("bearer_token", Case.Sensitive);
        snapshots.ShouldNotContain("api-key-value", Case.Sensitive);
        snapshots.ShouldNotContain("bearer-token-value", Case.Sensitive);
    }

    private async Task AssertRetryDeadLetterReplayAndSnapshotsAsync(
        TenantContext tenant,
        Guid source,
        Guid topic)
    {
        Guid successDestination = await CreateConnectionAsync(
            tenant, HttpConnectorId, "independent-success", "http://mocksink:8080/sink/independent-success");
        Guid failureDestination = await CreateConnectionAsync(
            tenant, HttpConnectorId, "independent-failure", "http://mocksink:8080/sink/independent-failure");
        Guid successSubscription = await CreateSubscriptionAsync(
            tenant, topic, "independent-success", successDestination, "independent.test");
        Guid failureSubscription = await CreateSubscriptionAsync(
            tenant, topic, "independent-failure", failureDestination, "independent.test");

        using HttpResponseMessage failMode = await fixture.MockSinkClient.PutAsJsonAsync(
            "/control/independent-failure", new { mode = "fail" });
        failMode.StatusCode.ShouldBe(HttpStatusCode.OK);

        EventAcceptance accepted = await IngestAsync(
            tenant, source, "payments", "independent.test", new { sequence = 1 });
        await WaitForDeliveryStatusAsync(accepted.Id, successSubscription, "succeeded");
        await WaitForDeliveryStatusAsync(accepted.Id, failureSubscription, "dead_lettered");
        (await fixture.ScalarAsync<int>(
            $"SELECT lifetime_attempt_count FROM event_deliveries WHERE event_id = '{accepted.Id}' AND subscription_id = '{failureSubscription}'")).ShouldBe(3);

        using HttpResponseMessage recover = await fixture.MockSinkClient.DeleteAsync("/control/independent-failure");
        recover.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid failureDelivery = await fixture.ScalarAsync<Guid>(
            $"SELECT id FROM event_deliveries WHERE event_id = '{accepted.Id}' AND subscription_id = '{failureSubscription}'");
        using HttpResponseMessage replay = await SendAdminAsync(
            HttpMethod.Post,
            $"/admin/tenants/{tenant.Id}/events/{accepted.Id}/deliveries/{failureDelivery}/replay");
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForDeliveryStatusAsync(accepted.Id, failureSubscription, "succeeded");
        (await fixture.ScalarAsync<int>(
            $"SELECT lifetime_attempt_count FROM event_deliveries WHERE event_id = '{accepted.Id}' AND subscription_id = '{failureSubscription}'")).ShouldBe(4);
        (await fixture.ScalarAsync<string>(
            $"SELECT string_agg(da.attempt_number::text, ',' ORDER BY da.attempt_number) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{accepted.Id}' AND sd.subscription_id = '{failureSubscription}'")).ShouldBe("1,2,3,4");

        Guid snapshotDestination = await CreateConnectionAsync(
            tenant, HttpConnectorId, "snapshot-destination", "http://mocksink:8080/sink/snapshot");
        Guid snapshotSubscription = await CreateSubscriptionAsync(
            tenant, topic, "snapshot", snapshotDestination, "snapshot.test", Jsonata("{ \"version\": \"first\" }"));
        using HttpResponseMessage snapshotFail = await fixture.MockSinkClient.PutAsJsonAsync(
            "/control/snapshot", new { mode = "fail" });
        snapshotFail.StatusCode.ShouldBe(HttpStatusCode.OK);
        EventAcceptance snapshotEvent = await IngestAsync(
            tenant, source, "payments", "snapshot.test", new { value = 1 });
        await WaitForAttemptCountAsync(snapshotEvent.Id, snapshotSubscription, 1);

        using HttpResponseMessage update = await PatchAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions/{snapshotSubscription}",
            SubscriptionBody("snapshot", snapshotDestination, "snapshot.test", Jsonata("{ \"version\": \"second\" }")));
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The failed attempt already left a receipt carrying the original expression. Clearing it
        // first is what makes the post-update assertion falsifiable: only the retry's body remains.
        using HttpResponseMessage clearSnapshotReceipts = await fixture.MockSinkClient.DeleteAsync("/receipts/snapshot");
        clearSnapshotReceipts.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage snapshotRecover = await fixture.MockSinkClient.DeleteAsync("/control/snapshot");
        snapshotRecover.StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForDeliveryStatusAsync(snapshotEvent.Id, snapshotSubscription, "succeeded");
        await AssertReceiptBodyAsync("snapshot", "{\"version\":\"first\"}");
        await AssertNoReceiptBodyContainsAsync("snapshot", "second");

        Guid runtimeDestination = await CreateConnectionAsync(
            tenant, HttpConnectorId, "runtime-transform", "http://mocksink:8080/sink/runtime-transform");
        Guid runtimeSubscription = await CreateSubscriptionAsync(
            tenant, topic, "runtime-transform", runtimeDestination, "runtime.transform", Jsonata("$error(\"acceptance runtime failure\")"));
        EventAcceptance runtimeEvent = await IngestAsync(
            tenant, source, "payments", "runtime.transform", new { value = 1 });
        await WaitForAttemptCountAsync(runtimeEvent.Id, runtimeSubscription, 1);
        (await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{runtimeEvent.Id}' AND sd.subscription_id = '{runtimeSubscription}' ORDER BY da.attempt_number LIMIT 1")).ShouldBe("transform");
    }

    private async Task AssertDestinationBoundaryAsync(TenantContext tenant, Guid source, Guid topic)
    {
        using HttpResponseMessage relative = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(HttpConnectorId, "relative-url", "/relative"));
        JsonElement relativeBody = await AssertJsonAsync(relative, HttpStatusCode.Created);
        using HttpResponseMessage relativeRejected = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("relative-url", relativeBody.GetProperty("id").GetGuid(), "relative.test"));
        relativeRejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        using HttpResponseMessage ftp = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(HttpConnectorId, "ftp-url", "ftp://example.test/file"));
        JsonElement ftpBody = await AssertJsonAsync(ftp, HttpStatusCode.Created);
        using HttpResponseMessage ftpRejected = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("ftp-url", ftpBody.GetProperty("id").GetGuid(), "ftp.test"));
        ftpRejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        using HttpResponseMessage privateDestination = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(HttpConnectorId, "operator-loopback", "http://127.0.0.1:1/private"));
        privateDestination.StatusCode.ShouldBe(HttpStatusCode.Created);

        Guid sourceOnlyConnection = await CreateConnectionAsync(
            tenant, SourceOnlyConnectorId, "source-only-destination", "http://mocksink:8080/sink/source-only");
        using HttpResponseMessage directionRejected = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("source-only", sourceOnlyConnection, "direction.test"));
        directionRejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await fixture.WriteSecretAsync(tenant.Slug, "redirect_secret", "redirect-secret-value");
        Guid redirectDestination = await CreateConnectionAsync(
            tenant,
            ApiKeyConnectorId,
            "redirect-destination",
            "http://mocksink:8080/redirect/redirect-target",
            ApiKeyAuth("redirect_secret"));
        Guid redirectSubscription = await CreateSubscriptionAsync(
            tenant, topic, "redirect", redirectDestination, "redirect.test");
        EventAcceptance redirected = await IngestAsync(
            tenant, source, "payments", "redirect.test", new { value = 1 });
        await WaitForAttemptCountAsync(redirected.Id, redirectSubscription, 1);
        (await fixture.ScalarAsync<int>(
            $"SELECT da.response_status_code FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{redirected.Id}' AND sd.subscription_id = '{redirectSubscription}' ORDER BY da.attempt_number LIMIT 1")).ShouldBe(307);
        // Recording the 307 is not enough: a redirect must also be a failed delivery, otherwise the
        // Worker would be treating an unvisited destination as a satisfied one.
        (await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{redirected.Id}' AND sd.subscription_id = '{redirectSubscription}' ORDER BY da.attempt_number LIMIT 1")).ShouldBe("http");
        (await fixture.ScalarAsync<string>(
            $"SELECT status FROM event_deliveries WHERE event_id = '{redirected.Id}' AND subscription_id = '{redirectSubscription}'")).ShouldNotBe("succeeded");
        (await ReceiptCountAsync("redirect-target")).ShouldBe(0);

        Guid slowDestination = await CreateConnectionAsync(
            tenant, HttpConnectorId, "slow-destination", "http://mocksink:8080/sink/slow-timeout");
        Guid slowSubscription = await CreateSubscriptionAsync(
            tenant, topic, "slow-timeout", slowDestination, "slow.test");
        // Must outlast the deployment's compressed HttpTimeout so the client, not the sink, ends
        // the attempt.
        using HttpResponseMessage slowMode = await fixture.MockSinkClient.PutAsJsonAsync(
            "/control/slow-timeout", new { mode = "slow", delayMs = 8000 });
        slowMode.StatusCode.ShouldBe(HttpStatusCode.OK);
        EventAcceptance slowEvent = await IngestAsync(
            tenant, source, "payments", "slow.test", new { value = 1 });
        await WaitForAttemptCountAsync(slowEvent.Id, slowSubscription, 1);
        (await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{slowEvent.Id}' AND sd.subscription_id = '{slowSubscription}' ORDER BY da.attempt_number LIMIT 1")).ShouldBe("http");
        (await fixture.ScalarAsync<string>(
            $"SELECT da.error_message FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{slowEvent.Id}' AND sd.subscription_id = '{slowSubscription}' ORDER BY da.attempt_number LIMIT 1")).ShouldBe("Request timed out");

        // Leaving the sink slow would make every later retry of this subscription hold a delivery
        // slot for the full outbound timeout, eating into the evidence budget of later sections.
        using HttpResponseMessage slowRecover = await fixture.MockSinkClient.DeleteAsync("/control/slow-timeout");
        slowRecover.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task AssertDrainBeforeChangeAsync()
    {
        TenantContext tenant = await CreateTenantAsync("acceptance-drain");
        (Guid source, Guid topic) = await CreateSourceTopicAsync(tenant, "drain");
        Guid sourceConnection = await fixture.ScalarAsync<Guid>($"SELECT connection_id FROM sources WHERE id = '{source}'");
        Guid destination = await CreateConnectionAsync(
            tenant, HttpConnectorId, "drain-destination", "http://mocksink:8080/sink/drain");
        Guid subscription = await CreateSubscriptionAsync(
            tenant, topic, "drain-subscription", destination, "drain.test");
        EventAcceptance accepted = await IngestAsync(
            tenant, source, "drain", "drain.test", new { value = 1 });

        await WaitForDeliveryStatusAsync(accepted.Id, subscription, "succeeded");
        (await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM outbox WHERE event_id = '{accepted.Id}' AND processed_at IS NULL")).ShouldBe(0L);

        foreach (string path in new[]
        {
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions/{subscription}/deactivate",
            $"/admin/tenants/{tenant.Id}/connections/{destination}/deactivate",
            $"/admin/tenants/{tenant.Id}/topics/{topic}/deactivate",
            $"/admin/tenants/{tenant.Id}/connections/{sourceConnection}/deactivate"
        })
        {
            using HttpResponseMessage deactivated = await PostAdminAsync(path, new { });
            deactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await fixture.ScalarAsync<string>(
            $"SELECT status FROM event_deliveries WHERE event_id = '{accepted.Id}' AND subscription_id = '{subscription}'")).ShouldBe("succeeded");
    }

    private async Task AssertSecretProvidersAsync()
    {
        TenantContext fileA = await CreateTenantAsync("acceptance-file-a");
        TenantContext fileB = await CreateTenantAsync("acceptance-file-b");
        await fixture.WriteSecretAsync(fileA.Slug, "shared_secret", "file-a-value");
        await fixture.WriteSecretAsync(fileB.Slug, "shared_secret", "file-b-value");
        await fixture.RecreateWorkerAsync("file");

        await AssertAuthenticatedTenantDeliveryAsync(fileA, "file-a", "shared_secret", "file-a-value");
        await AssertAuthenticatedTenantDeliveryAsync(fileB, "file-b", "shared_secret", "file-b-value");

        TenantContext rotation = await CreateTenantAsync("acceptance-rotation");
        fixture.RotateSecretSymlink(rotation.Slug, "shared_secret", "secret-v1", "rotation-v1");
        (Guid rotationSource, Guid rotationTopic) = await CreateSourceTopicAsync(rotation, "rotation");
        Guid rotationDestination = await CreateConnectionAsync(
            rotation, ApiKeyConnectorId, "rotation-destination", "http://mocksink:8080/sink/rotation", ApiKeyAuth("shared_secret"));
        Guid rotationSubscription = await CreateSubscriptionAsync(
            rotation, rotationTopic, "rotation", rotationDestination, "rotation.test");
        using HttpResponseMessage fail = await fixture.MockSinkClient.PutAsJsonAsync("/control/rotation", new { mode = "fail" });
        fail.StatusCode.ShouldBe(HttpStatusCode.OK);
        EventAcceptance rotationEvent = await IngestAsync(rotation, rotationSource, "rotation", "rotation.test", new { value = 1 });
        await WaitForAttemptCountAsync(rotationEvent.Id, rotationSubscription, 1);
        await AssertReceiptHeaderAsync("rotation", "X-Api-Key", "rotation-v1");
        using HttpResponseMessage resetReceipts = await fixture.MockSinkClient.DeleteAsync("/receipts/rotation");
        resetReceipts.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.RotateSecretSymlink(rotation.Slug, "shared_secret", "secret-v2", "rotation-v2");
        using HttpResponseMessage recover = await fixture.MockSinkClient.DeleteAsync("/control/rotation");
        recover.StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForDeliveryStatusAsync(rotationEvent.Id, rotationSubscription, "succeeded");
        await AssertReceiptHeaderAsync("rotation", "X-Api-Key", "rotation-v2");

        TenantContext configuration = await CreateTenantAsync("acceptance-config");
        await fixture.WriteSecretAsync(configuration.Slug, "shared_secret", "wrong-file-value");
        await fixture.RecreateWorkerAsync("configuration", "configuration-value", "configuration-only-value");
        await AssertAuthenticatedTenantDeliveryAsync(
            configuration, "configuration", "shared_secret", "configuration-value");

        await fixture.WriteSecretAsync(configuration.Slug, "file_only", "file-only-value");
        await AssertMissingSecretFailsAsync(configuration, "configuration-no-file-fallback", "file_only");

        ComposeResult configCli = await fixture.RunWorkerCommandAsync(
            new Dictionary<string, string?>
            {
                ["Integrios__DestinationSecrets__Provider"] = "configuration",
                ["DestinationSecrets__acceptance-config__shared_secret"] = "configuration-value"
            },
            "secrets", "validate", "--tenant", configuration.Slug);
        configCli.ExitCode.ShouldBe(1);
        configCli.Output.ShouldNotContain("configuration-value", Case.Sensitive);

        await fixture.RecreateWorkerAsync("file", "configuration-value", "configuration-only-value");
        await AssertMissingSecretFailsAsync(configuration, "file-no-config-fallback", "config_only");

        await fixture.WriteSecretAsync(configuration.Slug, "line_break", "unsafe\r\nvalue");
        await AssertMissingSecretFailsAsync(configuration, "line-break", "line_break", "request_construction");

        ComposeResult fileCli = await fixture.RunWorkerCommandAsync(
            new Dictionary<string, string?> { ["Integrios__DestinationSecrets__Provider"] = "file" },
            "secrets", "validate", "--tenant", fileA.Slug);
        fileCli.ExitCode.ShouldBe(0);
        fileCli.Output.ShouldNotContain("file-a-value", Case.Sensitive);

        ComposeResult fullDeploymentCli = await fixture.RunWorkerCommandAsync(
            new Dictionary<string, string?> { ["Integrios__DestinationSecrets__Provider"] = "file" },
            "secrets", "validate", "--all");
        fullDeploymentCli.ExitCode.ShouldBe(1);
        fullDeploymentCli.Output.ShouldNotContain("file-a-value", Case.Sensitive);
        fullDeploymentCli.Output.ShouldNotContain("file-b-value", Case.Sensitive);
        fullDeploymentCli.Output.ShouldNotContain("rotation-v2", Case.Sensitive);
    }

    private async Task AssertBootstrapRestartAndOperatorKeyRotationAsync()
    {
        long operatorKeysBefore = await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM operator_keys");
        await fixture.RunBootstrapAgainAsync();
        (await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM operator_keys")).ShouldBe(operatorKeysBefore);
        (await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM connectors WHERE key = 'http'")).ShouldBe(1L);

        await fixture.RestartProductServicesAsync();
        using HttpResponseMessage healthyAdmin = await fixture.AdminClient.GetAsync("/health");
        using HttpResponseMessage healthyIngestion = await fixture.IngestionClient.GetAsync("/health");
        healthyAdmin.StatusCode.ShouldBe(HttpStatusCode.OK);
        healthyIngestion.StatusCode.ShouldBe(HttpStatusCode.OK);

        string oldAuthorization = fixture.AdminAuthorization;
        string rotatedSecret = $"rotated-{Suffix()}";
        string publicKey = await fixture.RotateOperatorKeyAsync(rotatedSecret);
        publicKey.ShouldNotBe("global_operator_key");

        using var oldRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        oldRequest.Headers.TryAddWithoutValidation("Authorization", oldAuthorization);
        using HttpResponseMessage revoked = await fixture.AdminClient.SendAsync(oldRequest);
        revoked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpResponseMessage rotated = await SendAdminAsync(HttpMethod.Get, "/admin/tenants");
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task AssertSecretsAbsentFromDurableEvidenceAsync()
    {
        const string probes = "api-key-value|bearer-token-value|file-a-value|file-b-value|rotation-v1|rotation-v2|configuration-value|configuration-only-value|file-only-value|unsafe";
        (await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM connections WHERE config::text ~ '{probes}' OR COALESCE(source_verification::text, '') ~ '{probes}' OR COALESCE(destination_authentication::text, '') ~ '{probes}'")).ShouldBe(0L);
        (await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts WHERE COALESCE(error_message, '') ~ '{probes}'")).ShouldBe(0L);

        string logs = string.Join('\n',
            await fixture.GetServiceLogsAsync("admin"),
            await fixture.GetServiceLogsAsync("ingestion"),
            await fixture.GetServiceLogsAsync("worker"));
        // The CR/LF secret is probed by its distinctive prefix, not its full text: Compose prefixes
        // every log line with the service name, so the literal value could never appear contiguously
        // and a full-text probe would pass vacuously.
        foreach (string secret in new[]
        {
            "api-key-value", "bearer-token-value", "file-a-value", "file-b-value", "rotation-v1",
            "rotation-v2", "configuration-value", "configuration-only-value", "file-only-value", "unsafe"
        })
        {
            logs.ShouldNotContain(secret, Case.Sensitive);
        }
    }

    private async Task InstallAcceptanceConnectorsAsync() => await fixture.ExecuteAsync($$"""
        INSERT INTO connectors (
            id, key, contract_version, manifest_schema_version, name, direction,
            status, description, manifest)
        VALUES
            ('{{ApiKeyConnectorId}}', 'acceptance_api_key', 1, 1, 'Acceptance API key', 'destination', 'active', 'Acceptance-only connector', '{{TestConnectorManifest.Create("acceptance_api_key", "Acceptance API key", "destination", ["api_key_header"])}}'::jsonb),
            ('{{BearerConnectorId}}', 'acceptance_bearer', 1, 1, 'Acceptance bearer', 'destination', 'active', 'Acceptance-only connector', '{{TestConnectorManifest.Create("acceptance_bearer", "Acceptance bearer", "destination", ["bearer_token"])}}'::jsonb),
            ('{{SourceOnlyConnectorId}}', 'acceptance_source', 1, 1, 'Acceptance source', 'source', 'active', 'Acceptance-only connector', '{{TestConnectorManifest.Create("acceptance_source", "Acceptance source", "source")}}'::jsonb)
        ON CONFLICT (id) DO NOTHING;
        """);

    private async Task<TenantContext> CreateTenantAsync(string slug)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            "/admin/tenants",
            new { slug, name = $"Acceptance {slug}", environment = "production" });
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.Created);
        Guid id = body.GetProperty("id").GetGuid();

        using HttpResponseMessage tenantApiKey = await PostAdminAsync(
            $"/admin/tenants/{id}/tenant-api-keys",
            new { name = "acceptance-ingestion" });
        JsonElement tenantApiKeyBody = await AssertJsonAsync(tenantApiKey, HttpStatusCode.Created);
        tenantApiKeyBody.GetProperty("tenant_api_key").TryGetProperty("scopes", out _).ShouldBeFalse();
        return new TenantContext(
            id,
            slug,
            tenantApiKeyBody.GetProperty("tenant_api_key").GetProperty("id").GetGuid(),
            tenantApiKeyBody.GetProperty("token").GetString()!);
    }

    private async Task<Guid> CreateConnectionAsync(
        TenantContext tenant,
        string connectorId,
        string name,
        string url,
        object? auth = null)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(connectorId, name, url, auth));
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateTopicAsync(TenantContext tenant, string name)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics",
            new { name });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateEventApiSourceAsync(TenantContext tenant, Guid connection, Guid topic)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/sources",
            new { connection_id = connection, topic_id = topic, type = "event_api", configuration = new { source_contract = "event_json" } });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateSubscriptionAsync(
        TenantContext tenant,
        Guid topic,
        string name,
        Guid destination,
        string eventType,
        object? transform = null)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody(name, destination, eventType, transform));
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid Source, Guid Topic)> CreateSourceTopicAsync(TenantContext tenant, string topicName)
    {
        Guid sourceConnection = await CreateConnectionAsync(
            tenant, HttpConnectorId, $"{topicName}-source", $"http://mocksink:8080/sink/{topicName}-source");
        Guid topic = await CreateTopicAsync(tenant, topicName);
        Guid source = await CreateEventApiSourceAsync(tenant, sourceConnection, topic);
        return (source, topic);
    }

    private async Task AssertAuthenticatedTenantDeliveryAsync(
        TenantContext tenant,
        string sink,
        string secretReference,
        string expectedValue)
    {
        (Guid source, Guid topic) = await CreateSourceTopicAsync(tenant, sink);
        Guid destination = await CreateConnectionAsync(
            tenant, ApiKeyConnectorId, $"{sink}-destination", $"http://mocksink:8080/sink/{sink}", ApiKeyAuth(secretReference));
        Guid subscription = await CreateSubscriptionAsync(
            tenant, topic, $"{sink}-subscription", destination, $"{sink}.test");
        EventAcceptance accepted = await IngestAsync(
            tenant, source, sink, $"{sink}.test", new { sink });
        await WaitForDeliveryStatusAsync(accepted.Id, subscription, "succeeded");
        await AssertReceiptHeaderAsync(sink, "X-Api-Key", expectedValue);
    }

    private async Task AssertMissingSecretFailsAsync(
        TenantContext tenant,
        string sink,
        string secretReference,
        string expectedPhase = "secret_resolution")
    {
        (Guid source, Guid topic) = await CreateSourceTopicAsync(tenant, sink);
        Guid destination = await CreateConnectionAsync(
            tenant, ApiKeyConnectorId, $"{sink}-destination", $"http://mocksink:8080/sink/{sink}", ApiKeyAuth(secretReference));
        Guid subscription = await CreateSubscriptionAsync(
            tenant, topic, $"{sink}-subscription", destination, $"{sink}.test");
        EventAcceptance accepted = await IngestAsync(
            tenant, source, sink, $"{sink}.test", new { sink });
        await WaitForAttemptCountAsync(accepted.Id, subscription, 1);
        (await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{accepted.Id}' AND sd.subscription_id = '{subscription}' ORDER BY da.attempt_number LIMIT 1")).ShouldBe(expectedPhase);
        (await ReceiptCountAsync(sink)).ShouldBe(0);
    }

    private async Task<EventAcceptance> IngestAsync(
        TenantContext tenant,
        Guid sourceId,
        string topicName,
        string eventType,
        object payload,
        string? idempotencyKey = null)
    {
        using HttpResponseMessage response = await SendIngestionAsync(
            tenant,
            HttpMethod.Post,
            $"/events?source_id={sourceId}",
            new
            {
                event_type = eventType,
                source_event_id = idempotencyKey ?? $"acceptance-{Guid.NewGuid():N}",
                payload,
            });
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.Accepted);
        return new EventAcceptance(
            body.GetProperty("event_id").GetGuid(),
            body.GetProperty("already_accepted").GetBoolean());
    }

    private async Task AssertAcceptanceRejectedAsync(TenantContext tenant, Guid sourceId, string topicName)
    {
        using HttpResponseMessage response = await SendIngestionAsync(
            tenant,
            HttpMethod.Post,
            $"/events?source_id={sourceId}",
            new
            {
                event_type = "rejected.test",
                source_event_id = $"acceptance-{Guid.NewGuid():N}",
                payload = new { rejected = true },
            });
        // An inactive/foreign/unassociated Source id no longer resolves at all, so rejection is now
        // 404 (matches the webhook/queue "no active Source" convention), not 422.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> PostAdminAsync(string path, object body) =>
        await SendAdminAsync(HttpMethod.Post, path, body);

    private async Task<HttpResponseMessage> PatchAdminAsync(string path, object body) =>
        await SendAdminAsync(HttpMethod.Patch, path, body);

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await fixture.AdminClient.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendIngestionAsync(
        TenantContext tenant,
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", $"TenantApiKey {tenant.ApiToken}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await fixture.IngestionClient.SendAsync(request);
    }

    private static async Task<JsonElement> AssertJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        string body = await response.Content.ReadAsStringAsync();
        (response.StatusCode == expected).ShouldBeTrue($"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task WaitForDeliveryStatusAsync(Guid eventId, Guid subscriptionId, string expected) =>
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM event_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'") == expected);

    private async Task WaitForAttemptCountAsync(Guid eventId, Guid subscriptionId, int minimum) =>
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}' AND sd.subscription_id = '{subscriptionId}' AND da.status <> 'in_progress'") >= minimum);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = Stopwatch.StartNew();
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
            await Task.Delay(PollInterval);
        }
        throw new TimeoutException($"Acceptance evidence was not ready within {EvidenceTimeout}. {lastException?.Message}");
    }

    private async Task AssertReceiptHeaderAsync(string sink, string name, string value)
    {
        using HttpResponseMessage assertion = await fixture.MockSinkClient.PostAsJsonAsync(
            $"/receipts/{sink}/assert-headers",
            new { headers = new Dictionary<string, string> { [name] = value } });
        string body = await assertion.Content.ReadAsStringAsync();
        assertion.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotContain(value, Case.Sensitive);
    }

    private async Task AssertReceiptBodyAsync(string sink, string expected)
    {
        using JsonDocument receipts = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>($"/receipts/{sink}")
            ?? throw new InvalidOperationException("MockSink returned no receipt evidence.");
        receipts.RootElement.GetProperty("receipts").EnumerateArray().ShouldContain(
            receipt => JsonEquivalent(receipt.GetProperty("body").GetString()!, expected));
    }

    private async Task AssertNoReceiptBodyContainsAsync(string sink, string fragment)
    {
        using JsonDocument receipts = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>($"/receipts/{sink}")
            ?? throw new InvalidOperationException("MockSink returned no receipt evidence.");
        receipts.RootElement.GetProperty("receipts").EnumerateArray().ShouldNotContain(
            receipt => receipt.GetProperty("body").GetString()!.Contains(fragment, StringComparison.Ordinal));
    }

    private async Task<int> ReceiptCountAsync(string sink)
    {
        using JsonDocument receipts = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>($"/receipts/{sink}")
            ?? throw new InvalidOperationException("MockSink returned no receipt evidence.");
        return receipts.RootElement.GetProperty("count").GetInt32();
    }

    private static bool JsonEquivalent(string actual, string expected)
    {
        using JsonDocument actualDocument = JsonDocument.Parse(actual);
        using JsonDocument expectedDocument = JsonDocument.Parse(expected);
        return JsonElement.DeepEquals(actualDocument.RootElement, expectedDocument.RootElement);
    }

    private static object ConnectionBody(string connectorId, string name, string url, object? auth = null) => new
    {
        connector_id = connectorId,
        name,
        config = new { base_uri = url },
        destination_authentication = auth,
        environment = "production"
    };

    private static object SubscriptionBody(string name, Guid destinationConnectionId, string eventType, object? transform = null) => new
    {
        name,
        match_rules = new { event_type = eventType },
        destination_connection_id = destinationConnectionId,
        mapping = transform,
        order_index = 10
    };

    private static object ApiKeyAuth(string secretReference) => new
    {
        scheme = "api_key_header",
        config = new { header_name = "X-Api-Key" },
        secret_refs = new { api_key = secretReference }
    };

    private static object BearerAuth(string secretReference) => new
    {
        scheme = "bearer_token",
        config = new { },
        secret_refs = new { token = secretReference }
    };

    private static object Jsonata(string expression) => new { engine = "jsonata", version = "1", expression };
    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private sealed record TenantContext(Guid Id, string Slug, Guid TenantApiKeyId, string ApiToken);
    private sealed record EventAcceptance(Guid Id, bool AlreadyAccepted);
}
