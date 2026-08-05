using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Integrios.AcceptanceTests;

[Collection(PackagedDeploymentCollection.Name)]
[Trait("Category", "Qualification")]
public sealed class LiveProductBehaviorTests(PackagedDeploymentFixture fixture)
{
    private const string HttpIntegrationId = "00000000-0000-0000-0000-000000000001";
    private const string ApiKeyIntegrationId = "11111111-1111-1111-1111-111111111111";
    private const string BearerIntegrationId = "22222222-2222-2222-2222-222222222222";
    private const string SourceOnlyIntegrationId = "33333333-3333-3333-3333-333333333333";
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task PackagedSystem_QualifiesLiveProductBehaviorMatrix()
    {
        await InstallQualificationIntegrationsAsync();
        await AssertCredentialSchemaAndControlPlaneAuthorityAsync();

        TenantContext primary = await CreateTenantAsync($"matrix-{Suffix()}");
        TenantContext isolated = await CreateTenantAsync($"isolated-{Suffix()}");
        await AssertTenantAndApiKeyContractsAsync(primary, isolated);

        Guid source = await CreateConnectionAsync(primary, HttpIntegrationId, "source", "http://mocksink:8080/sink/source");
        Guid topic = await CreateTopicAsync(primary, "payments", [source]);
        await AssertSourceConnectionAndTopicContractsAsync(primary, isolated, source, topic);

        await AssertUnroutedAndIdempotentAcceptanceAsync(primary, source);
        await AssertFanoutTransformsAndAuthenticatedDeliveryAsync(primary, source, topic);
        await AssertRetryDeadLetterReplayAndSnapshotsAsync(primary, source, topic);
        await AssertDestinationBoundaryAsync(primary, source, topic);
        await AssertDrainBeforeChangeAsync();
        await AssertSecretProvidersAsync();
        await AssertBootstrapRestartAndAdminKeyRotationAsync();
        await AssertSecretsAbsentFromDurableEvidenceAsync();
    }

    private async Task AssertCredentialSchemaAndControlPlaneAuthorityAsync()
    {
        using HttpResponseMessage unauthenticated = await fixture.AdminClient.GetAsync("/admin/tenants");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using HttpResponseMessage authenticated = await SendAdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'admin_keys' AND column_name = 'tenant_id'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'api_keys' AND column_name = 'scopes'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'subscriptions' AND column_name IN ('delivery_policy', 'dlq_enabled')"));
    }

    private async Task AssertTenantAndApiKeyContractsAsync(TenantContext primary, TenantContext isolated)
    {
        using HttpResponseMessage invalidSlug = await PostAdminAsync(
            "/admin/tenants",
            new { slug = "Invalid_Slug", name = "Invalid", environment = "production" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidSlug.StatusCode);

        using HttpResponseMessage duplicateTenant = await PostAdminAsync(
            "/admin/tenants",
            new { slug = primary.Slug, name = "Duplicate", environment = "production" });
        Assert.Equal(HttpStatusCode.Conflict, duplicateTenant.StatusCode);

        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync($$"""
            INSERT INTO tenants (id, slug, name, status, created_at, updated_at)
            VALUES ('{{Guid.NewGuid()}}', 'Invalid_Database_Slug', 'Invalid', 'active', now(), now())
            """));

        using HttpResponseMessage apiKeys = await SendAdminAsync(
            HttpMethod.Get,
            $"/admin/tenants/{primary.Id}/api-keys");
        string apiKeysBody = await apiKeys.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, apiKeys.StatusCode);
        Assert.DoesNotContain("scope", apiKeysBody, StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage spareKeyResponse = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/api-keys",
            new { name = "revocation-check" });
        JsonElement spareKey = await AssertJsonAsync(spareKeyResponse, HttpStatusCode.Created);
        Guid spareKeyId = spareKey.GetProperty("api_key").GetProperty("id").GetGuid();
        var spareContext = new TenantContext(primary.Id, primary.Slug, spareKeyId, spareKey.GetProperty("token").GetString()!);
        using HttpResponseMessage revoke = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/api-keys/{spareKeyId}/revoke",
            new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using HttpResponseMessage revokedDataPlane = await SendIngressAsync(
            spareContext, HttpMethod.Get, $"/events/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedDataPlane.StatusCode);

        Guid source = await CreateConnectionAsync(primary, HttpIntegrationId, "tenant-read-source", "http://mocksink:8080/sink/read-source");
        await CreateTopicAsync(primary, "tenant-reads", [source]);
        EventAcceptance accepted = await IngestAsync(primary, source, "tenant-reads", "read.test", new { ok = true });

        using HttpResponseMessage ownRead = await SendIngressAsync(primary, HttpMethod.Get, $"/events/{accepted.Id}");
        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
        using HttpResponseMessage otherRead = await SendIngressAsync(isolated, HttpMethod.Get, $"/events/{accepted.Id}");
        Assert.Equal(HttpStatusCode.NotFound, otherRead.StatusCode);
        using HttpResponseMessage otherReplay = await SendIngressAsync(isolated, HttpMethod.Post, $"/events/{accepted.Id}/replay");
        Assert.Equal(HttpStatusCode.NotFound, otherReplay.StatusCode);
    }

    private async Task AssertSourceConnectionAndTopicContractsAsync(
        TenantContext primary,
        TenantContext isolated,
        Guid validSource,
        Guid topic)
    {
        using HttpResponseMessage duplicateTopic = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/topics",
            new { name = "payments", source_connection_ids = new[] { validSource } });
        Assert.Equal(HttpStatusCode.Conflict, duplicateTopic.StatusCode);

        Guid isolatedSource = await CreateConnectionAsync(isolated, HttpIntegrationId, "isolated-source", "http://mocksink:8080/sink/isolated-source");
        await CreateTopicAsync(isolated, "isolated-topic", [isolatedSource]);
        await AssertAcceptanceRejectedAsync(primary, isolatedSource, "payments");

        Guid unassociated = await CreateConnectionAsync(primary, HttpIntegrationId, "unassociated-source", "http://mocksink:8080/sink/unassociated");
        await AssertAcceptanceRejectedAsync(primary, unassociated, "payments");

        Guid destinationOnly = await CreateConnectionAsync(
            primary,
            ApiKeyIntegrationId,
            "destination-only-source",
            "http://mocksink:8080/sink/destination-only",
            ApiKeyAuth("shared_secret"));
        await fixture.ExecuteAsync(
            $"INSERT INTO topic_sources (tenant_id, topic_id, connection_id) VALUES ('{primary.Id}', '{topic}', '{destinationOnly}')");
        await AssertAcceptanceRejectedAsync(primary, destinationOnly, "payments");

        Guid inactive = await CreateConnectionAsync(primary, HttpIntegrationId, "inactive-source", "http://mocksink:8080/sink/inactive");
        await fixture.ExecuteAsync(
            $"INSERT INTO topic_sources (tenant_id, topic_id, connection_id) VALUES ('{primary.Id}', '{topic}', '{inactive}')");
        using HttpResponseMessage deactivate = await PostAdminAsync(
            $"/admin/tenants/{primary.Id}/connections/{inactive}/deactivate",
            new { });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        await AssertAcceptanceRejectedAsync(primary, inactive, "payments");

        Assert.Equal("payments", await fixture.ScalarAsync<string>($"SELECT name FROM topics WHERE id = '{topic}'"));
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
        Assert.Equal(first.Id, duplicate.Id);
        Assert.False(first.AlreadyAccepted);
        Assert.True(duplicate.AlreadyAccepted);

        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>($"SELECT status FROM events WHERE id = '{first.Id}'") == "unrouted");
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM subscription_deliveries WHERE event_id = '{first.Id}'"));
    }

    private async Task AssertFanoutTransformsAndAuthenticatedDeliveryAsync(
        TenantContext tenant,
        Guid source,
        Guid topic)
    {
        await fixture.WriteSecretAsync(tenant.Slug, "api_key", "api-key-value");
        await fixture.WriteSecretAsync(tenant.Slug, "bearer_token", "bearer-token-value");

        Guid transformedDestination = await CreateConnectionAsync(
            tenant, HttpIntegrationId, "transform-destination", "http://mocksink:8080/sink/transform");
        Guid apiDestination = await CreateConnectionAsync(
            tenant, ApiKeyIntegrationId, "api-destination", "http://mocksink:8080/sink/api-auth", ApiKeyAuth("api_key"));
        Guid bearerDestination = await CreateConnectionAsync(
            tenant, BearerIntegrationId, "bearer-destination", "http://mocksink:8080/sink/bearer-auth", BearerAuth("bearer_token"));

        object transform = Jsonata("{ \"kind\": $context.event_type, \"amount\": amount, \"topic\": $context.topic_name }");
        using HttpResponseMessage preview = await PostAdminAsync(
            "/admin/transform/preview",
            new
            {
                transform,
                sampleInput = new { amount = 42 },
                sampleContext = new { event_type = "payment.created", topic_name = "payments" }
            });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        using HttpResponseMessage invalidTransform = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("invalid-transform", transformedDestination, "payment.created", Jsonata("{")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidTransform.StatusCode);

        string oversizedExpression = new('x', 65537);
        using HttpResponseMessage oversizedTransform = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("oversized-transform", transformedDestination, "payment.created", Jsonata(oversizedExpression)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oversizedTransform.StatusCode);

        Guid transformedSubscription = await CreateSubscriptionAsync(
            tenant, topic, "transformed", transformedDestination, "payment.created", transform);
        Guid apiSubscription = await CreateSubscriptionAsync(tenant, topic, "api-auth", apiDestination, "payment.created");
        Guid bearerSubscription = await CreateSubscriptionAsync(tenant, topic, "bearer-auth", bearerDestination, "payment.created");

        EventAcceptance accepted = await IngestAsync(
            tenant, source, "payments", "payment.created", new { amount = 42, paymentId = "pay-live" });
        await WaitForDeliveryStatusAsync(accepted.Id, transformedSubscription, "succeeded");
        await WaitForDeliveryStatusAsync(accepted.Id, apiSubscription, "succeeded");
        await WaitForDeliveryStatusAsync(accepted.Id, bearerSubscription, "succeeded");

        Assert.Equal(3L, await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM subscription_deliveries WHERE event_id = '{accepted.Id}'"));
        await AssertReceiptBodyAsync("transform", "{\"kind\":\"payment.created\",\"amount\":42,\"topic\":\"payments\"}");
        await AssertReceiptHeaderAsync("api-auth", "X-Api-Key", "api-key-value");
        await AssertReceiptHeaderAsync("bearer-auth", "Authorization", "Bearer bearer-token-value");

        string snapshots = await fixture.ScalarAsync<string>(
            $"SELECT string_agg(COALESCE(destination_auth::text, 'open'), '|') FROM subscription_deliveries WHERE event_id = '{accepted.Id}'");
        Assert.Contains("api_key_header", snapshots, StringComparison.Ordinal);
        Assert.Contains("bearer_token", snapshots, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-value", snapshots, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-token-value", snapshots, StringComparison.Ordinal);
    }

    private async Task AssertRetryDeadLetterReplayAndSnapshotsAsync(
        TenantContext tenant,
        Guid source,
        Guid topic)
    {
        Guid successDestination = await CreateConnectionAsync(
            tenant, HttpIntegrationId, "independent-success", "http://mocksink:8080/sink/independent-success");
        Guid failureDestination = await CreateConnectionAsync(
            tenant, HttpIntegrationId, "independent-failure", "http://mocksink:8080/sink/independent-failure");
        Guid successSubscription = await CreateSubscriptionAsync(
            tenant, topic, "independent-success", successDestination, "independent.test");
        Guid failureSubscription = await CreateSubscriptionAsync(
            tenant, topic, "independent-failure", failureDestination, "independent.test");

        using HttpResponseMessage failMode = await fixture.MockSinkClient.PutAsJsonAsync(
            "/control/independent-failure", new { mode = "fail" });
        Assert.Equal(HttpStatusCode.OK, failMode.StatusCode);

        EventAcceptance accepted = await IngestAsync(
            tenant, source, "payments", "independent.test", new { sequence = 1 });
        await WaitForDeliveryStatusAsync(accepted.Id, successSubscription, "succeeded");
        await WaitForDeliveryStatusAsync(accepted.Id, failureSubscription, "dead_lettered");
        Assert.Equal(3, await fixture.ScalarAsync<int>(
            $"SELECT lifetime_attempt_count FROM subscription_deliveries WHERE event_id = '{accepted.Id}' AND subscription_id = '{failureSubscription}'"));

        using HttpResponseMessage recover = await fixture.MockSinkClient.DeleteAsync("/control/independent-failure");
        Assert.Equal(HttpStatusCode.OK, recover.StatusCode);
        using HttpResponseMessage replay = await SendIngressAsync(tenant, HttpMethod.Post, $"/events/{accepted.Id}/replay");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        await WaitForDeliveryStatusAsync(accepted.Id, failureSubscription, "succeeded");
        Assert.Equal(4, await fixture.ScalarAsync<int>(
            $"SELECT lifetime_attempt_count FROM subscription_deliveries WHERE event_id = '{accepted.Id}' AND subscription_id = '{failureSubscription}'"));
        Assert.Equal("1,2,3,4", await fixture.ScalarAsync<string>(
            $"SELECT string_agg(da.attempt_number::text, ',' ORDER BY da.attempt_number) FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{accepted.Id}' AND sd.subscription_id = '{failureSubscription}'"));

        Guid snapshotDestination = await CreateConnectionAsync(
            tenant, HttpIntegrationId, "snapshot-destination", "http://mocksink:8080/sink/snapshot");
        Guid snapshotSubscription = await CreateSubscriptionAsync(
            tenant, topic, "snapshot", snapshotDestination, "snapshot.test", Jsonata("{ \"version\": \"first\" }"));
        using HttpResponseMessage snapshotFail = await fixture.MockSinkClient.PutAsJsonAsync(
            "/control/snapshot", new { mode = "fail" });
        Assert.Equal(HttpStatusCode.OK, snapshotFail.StatusCode);
        EventAcceptance snapshotEvent = await IngestAsync(
            tenant, source, "payments", "snapshot.test", new { value = 1 });
        await WaitForAttemptCountAsync(snapshotEvent.Id, snapshotSubscription, 1);

        using HttpResponseMessage update = await PatchAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions/{snapshotSubscription}",
            SubscriptionBody("snapshot", snapshotDestination, "snapshot.test", Jsonata("{ \"version\": \"second\" }")));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // The failed attempt already left a receipt carrying the original expression. Clearing it
        // first is what makes the post-update assertion falsifiable: only the retry's body remains.
        using HttpResponseMessage clearSnapshotReceipts = await fixture.MockSinkClient.DeleteAsync("/receipts/snapshot");
        Assert.Equal(HttpStatusCode.OK, clearSnapshotReceipts.StatusCode);

        using HttpResponseMessage snapshotRecover = await fixture.MockSinkClient.DeleteAsync("/control/snapshot");
        Assert.Equal(HttpStatusCode.OK, snapshotRecover.StatusCode);
        await WaitForDeliveryStatusAsync(snapshotEvent.Id, snapshotSubscription, "succeeded");
        await AssertReceiptBodyAsync("snapshot", "{\"version\":\"first\"}");
        await AssertNoReceiptBodyContainsAsync("snapshot", "second");

        Guid runtimeDestination = await CreateConnectionAsync(
            tenant, HttpIntegrationId, "runtime-transform", "http://mocksink:8080/sink/runtime-transform");
        Guid runtimeSubscription = await CreateSubscriptionAsync(
            tenant, topic, "runtime-transform", runtimeDestination, "runtime.transform", Jsonata("$error(\"qualification runtime failure\")"));
        EventAcceptance runtimeEvent = await IngestAsync(
            tenant, source, "payments", "runtime.transform", new { value = 1 });
        await WaitForAttemptCountAsync(runtimeEvent.Id, runtimeSubscription, 1);
        Assert.Equal("transform", await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{runtimeEvent.Id}' AND sd.subscription_id = '{runtimeSubscription}' ORDER BY da.attempt_number LIMIT 1"));
    }

    private async Task AssertDestinationBoundaryAsync(TenantContext tenant, Guid source, Guid topic)
    {
        using HttpResponseMessage relative = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(HttpIntegrationId, "relative-url", "/relative"));
        JsonElement relativeBody = await AssertJsonAsync(relative, HttpStatusCode.Created);
        using HttpResponseMessage relativeRejected = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("relative-url", relativeBody.GetProperty("id").GetGuid(), "relative.test"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, relativeRejected.StatusCode);

        using HttpResponseMessage ftp = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(HttpIntegrationId, "ftp-url", "ftp://example.test/file"));
        JsonElement ftpBody = await AssertJsonAsync(ftp, HttpStatusCode.Created);
        using HttpResponseMessage ftpRejected = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("ftp-url", ftpBody.GetProperty("id").GetGuid(), "ftp.test"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ftpRejected.StatusCode);

        using HttpResponseMessage privateDestination = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(HttpIntegrationId, "operator-loopback", "http://127.0.0.1:1/private"));
        Assert.Equal(HttpStatusCode.Created, privateDestination.StatusCode);

        Guid sourceOnlyConnection = await CreateConnectionAsync(
            tenant, SourceOnlyIntegrationId, "source-only-destination", "http://mocksink:8080/sink/source-only");
        using HttpResponseMessage directionRejected = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            SubscriptionBody("source-only", sourceOnlyConnection, "direction.test"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, directionRejected.StatusCode);

        await fixture.WriteSecretAsync(tenant.Slug, "redirect_secret", "redirect-secret-value");
        Guid redirectDestination = await CreateConnectionAsync(
            tenant,
            ApiKeyIntegrationId,
            "redirect-destination",
            "http://mocksink:8080/redirect/redirect-target",
            ApiKeyAuth("redirect_secret"));
        Guid redirectSubscription = await CreateSubscriptionAsync(
            tenant, topic, "redirect", redirectDestination, "redirect.test");
        EventAcceptance redirected = await IngestAsync(
            tenant, source, "payments", "redirect.test", new { value = 1 });
        await WaitForAttemptCountAsync(redirected.Id, redirectSubscription, 1);
        Assert.Equal(307, await fixture.ScalarAsync<int>(
            $"SELECT da.response_status_code FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{redirected.Id}' AND sd.subscription_id = '{redirectSubscription}' ORDER BY da.attempt_number LIMIT 1"));
        // Recording the 307 is not enough: a redirect must also be a failed delivery, otherwise the
        // Worker would be treating an unvisited destination as a satisfied one.
        Assert.Equal("http", await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{redirected.Id}' AND sd.subscription_id = '{redirectSubscription}' ORDER BY da.attempt_number LIMIT 1"));
        Assert.NotEqual("succeeded", await fixture.ScalarAsync<string>(
            $"SELECT status FROM subscription_deliveries WHERE event_id = '{redirected.Id}' AND subscription_id = '{redirectSubscription}'"));
        Assert.Equal(0, await ReceiptCountAsync("redirect-target"));

        Guid slowDestination = await CreateConnectionAsync(
            tenant, HttpIntegrationId, "slow-destination", "http://mocksink:8080/sink/slow-timeout");
        Guid slowSubscription = await CreateSubscriptionAsync(
            tenant, topic, "slow-timeout", slowDestination, "slow.test");
        // Must outlast the deployment's compressed HttpTimeout so the client, not the sink, ends
        // the attempt.
        using HttpResponseMessage slowMode = await fixture.MockSinkClient.PutAsJsonAsync(
            "/control/slow-timeout", new { mode = "slow", delayMs = 8000 });
        Assert.Equal(HttpStatusCode.OK, slowMode.StatusCode);
        EventAcceptance slowEvent = await IngestAsync(
            tenant, source, "payments", "slow.test", new { value = 1 });
        await WaitForAttemptCountAsync(slowEvent.Id, slowSubscription, 1);
        Assert.Equal("http", await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{slowEvent.Id}' AND sd.subscription_id = '{slowSubscription}' ORDER BY da.attempt_number LIMIT 1"));
        Assert.Equal("Request timed out", await fixture.ScalarAsync<string>(
            $"SELECT da.error_message FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{slowEvent.Id}' AND sd.subscription_id = '{slowSubscription}' ORDER BY da.attempt_number LIMIT 1"));

        // Leaving the sink slow would make every later retry of this subscription hold a delivery
        // slot for the full outbound timeout, eating into the evidence budget of later sections.
        using HttpResponseMessage slowRecover = await fixture.MockSinkClient.DeleteAsync("/control/slow-timeout");
        Assert.Equal(HttpStatusCode.OK, slowRecover.StatusCode);
    }

    private async Task AssertDrainBeforeChangeAsync()
    {
        TenantContext tenant = await CreateTenantAsync("qualification-drain");
        (Guid source, Guid topic) = await CreateSourceTopicAsync(tenant, "drain");
        Guid destination = await CreateConnectionAsync(
            tenant, HttpIntegrationId, "drain-destination", "http://mocksink:8080/sink/drain");
        Guid subscription = await CreateSubscriptionAsync(
            tenant, topic, "drain-subscription", destination, "drain.test");
        EventAcceptance accepted = await IngestAsync(
            tenant, source, "drain", "drain.test", new { value = 1 });

        await WaitForDeliveryStatusAsync(accepted.Id, subscription, "succeeded");
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM outbox WHERE event_id = '{accepted.Id}' AND processed_at IS NULL"));

        foreach (string path in new[]
        {
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions/{subscription}/deactivate",
            $"/admin/tenants/{tenant.Id}/connections/{destination}/deactivate",
            $"/admin/tenants/{tenant.Id}/topics/{topic}/deactivate",
            $"/admin/tenants/{tenant.Id}/connections/{source}/deactivate"
        })
        {
            using HttpResponseMessage deactivated = await PostAdminAsync(path, new { });
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        }

        Assert.Equal("succeeded", await fixture.ScalarAsync<string>(
            $"SELECT status FROM subscription_deliveries WHERE event_id = '{accepted.Id}' AND subscription_id = '{subscription}'"));
    }

    private async Task AssertSecretProvidersAsync()
    {
        TenantContext fileA = await CreateTenantAsync("qualification-file-a");
        TenantContext fileB = await CreateTenantAsync("qualification-file-b");
        await fixture.WriteSecretAsync(fileA.Slug, "shared_secret", "file-a-value");
        await fixture.WriteSecretAsync(fileB.Slug, "shared_secret", "file-b-value");
        await fixture.RecreateWorkerAsync("file");

        await AssertAuthenticatedTenantDeliveryAsync(fileA, "file-a", "shared_secret", "file-a-value");
        await AssertAuthenticatedTenantDeliveryAsync(fileB, "file-b", "shared_secret", "file-b-value");

        TenantContext rotation = await CreateTenantAsync("qualification-rotation");
        fixture.RotateSecretSymlink(rotation.Slug, "shared_secret", "secret-v1", "rotation-v1");
        (Guid rotationSource, Guid rotationTopic) = await CreateSourceTopicAsync(rotation, "rotation");
        Guid rotationDestination = await CreateConnectionAsync(
            rotation, ApiKeyIntegrationId, "rotation-destination", "http://mocksink:8080/sink/rotation", ApiKeyAuth("shared_secret"));
        Guid rotationSubscription = await CreateSubscriptionAsync(
            rotation, rotationTopic, "rotation", rotationDestination, "rotation.test");
        using HttpResponseMessage fail = await fixture.MockSinkClient.PutAsJsonAsync("/control/rotation", new { mode = "fail" });
        Assert.Equal(HttpStatusCode.OK, fail.StatusCode);
        EventAcceptance rotationEvent = await IngestAsync(rotation, rotationSource, "rotation", "rotation.test", new { value = 1 });
        await WaitForAttemptCountAsync(rotationEvent.Id, rotationSubscription, 1);
        await AssertReceiptHeaderAsync("rotation", "X-Api-Key", "rotation-v1");
        using HttpResponseMessage resetReceipts = await fixture.MockSinkClient.DeleteAsync("/receipts/rotation");
        Assert.Equal(HttpStatusCode.OK, resetReceipts.StatusCode);
        fixture.RotateSecretSymlink(rotation.Slug, "shared_secret", "secret-v2", "rotation-v2");
        using HttpResponseMessage recover = await fixture.MockSinkClient.DeleteAsync("/control/rotation");
        Assert.Equal(HttpStatusCode.OK, recover.StatusCode);
        await WaitForDeliveryStatusAsync(rotationEvent.Id, rotationSubscription, "succeeded");
        await AssertReceiptHeaderAsync("rotation", "X-Api-Key", "rotation-v2");

        TenantContext configuration = await CreateTenantAsync("qualification-config");
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
                ["DestinationSecrets__qualification-config__shared_secret"] = "configuration-value"
            },
            "secrets", "validate", "--tenant", configuration.Slug);
        Assert.Equal(1, configCli.ExitCode);
        Assert.DoesNotContain("configuration-value", configCli.Output, StringComparison.Ordinal);

        await fixture.RecreateWorkerAsync("file", "configuration-value", "configuration-only-value");
        await AssertMissingSecretFailsAsync(configuration, "file-no-config-fallback", "config_only");

        await fixture.WriteSecretAsync(configuration.Slug, "line_break", "unsafe\r\nvalue");
        await AssertMissingSecretFailsAsync(configuration, "line-break", "line_break", "request_construction");

        ComposeResult fileCli = await fixture.RunWorkerCommandAsync(
            new Dictionary<string, string?> { ["Integrios__DestinationSecrets__Provider"] = "file" },
            "secrets", "validate", "--tenant", fileA.Slug);
        Assert.Equal(0, fileCli.ExitCode);
        Assert.DoesNotContain("file-a-value", fileCli.Output, StringComparison.Ordinal);

        ComposeResult fullDeploymentCli = await fixture.RunWorkerCommandAsync(
            new Dictionary<string, string?> { ["Integrios__DestinationSecrets__Provider"] = "file" },
            "secrets", "validate", "--all");
        Assert.Equal(1, fullDeploymentCli.ExitCode);
        Assert.DoesNotContain("file-a-value", fullDeploymentCli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("file-b-value", fullDeploymentCli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("rotation-v2", fullDeploymentCli.Output, StringComparison.Ordinal);
    }

    private async Task AssertBootstrapRestartAndAdminKeyRotationAsync()
    {
        long adminKeysBefore = await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM admin_keys");
        await fixture.RunBootstrapAgainAsync();
        Assert.Equal(adminKeysBefore, await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM admin_keys"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM integrations WHERE key = 'http'"));

        await fixture.RestartProductServicesAsync();
        using HttpResponseMessage healthyAdmin = await fixture.AdminClient.GetAsync("/health");
        using HttpResponseMessage healthyIngress = await fixture.IngressClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthyAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, healthyIngress.StatusCode);

        string oldAuthorization = fixture.AdminAuthorization;
        string rotatedSecret = $"rotated-{Suffix()}";
        string publicKey = await fixture.RotateAdminKeyAsync(rotatedSecret);
        Assert.NotEqual("global_admin_key", publicKey);

        using var oldRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        oldRequest.Headers.TryAddWithoutValidation("Authorization", oldAuthorization);
        using HttpResponseMessage revoked = await fixture.AdminClient.SendAsync(oldRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using HttpResponseMessage rotated = await SendAdminAsync(HttpMethod.Get, "/admin/tenants");
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
    }

    private async Task AssertSecretsAbsentFromDurableEvidenceAsync()
    {
        const string probes = "api-key-value|bearer-token-value|file-a-value|file-b-value|rotation-v1|rotation-v2|configuration-value|configuration-only-value|file-only-value|unsafe";
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM connections WHERE config::text ~ '{probes}' OR COALESCE(source_verification::text, '') ~ '{probes}' OR COALESCE(destination_authentication::text, '') ~ '{probes}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts WHERE COALESCE(error_message, '') ~ '{probes}'"));

        string logs = string.Join('\n',
            await fixture.GetServiceLogsAsync("admin"),
            await fixture.GetServiceLogsAsync("ingress"),
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
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }

    private async Task InstallQualificationIntegrationsAsync() => await fixture.ExecuteAsync($$"""
        INSERT INTO integrations (
            id, key, contract_version, manifest_schema_version, name, direction,
            supported_auth_schemes, status, description, manifest)
        VALUES
            ('{{ApiKeyIntegrationId}}', 'qualification_api_key', 1, 1, 'Qualification API key', 'destination', '["api_key_header"]', 'active', 'Qualification-only integration', '{{TestIntegrationManifest.Create("qualification_api_key", "Qualification API key", "destination", "api_key_header")}}'::jsonb),
            ('{{BearerIntegrationId}}', 'qualification_bearer', 1, 1, 'Qualification bearer', 'destination', '["bearer_token"]', 'active', 'Qualification-only integration', '{{TestIntegrationManifest.Create("qualification_bearer", "Qualification bearer", "destination", "bearer_token")}}'::jsonb),
            ('{{SourceOnlyIntegrationId}}', 'qualification_source', 1, 1, 'Qualification source', 'source', '[]', 'active', 'Qualification-only integration', '{{TestIntegrationManifest.Create("qualification_source", "Qualification source", "source")}}'::jsonb)
        ON CONFLICT (id) DO NOTHING;
        """);

    private async Task<TenantContext> CreateTenantAsync(string slug)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            "/admin/tenants",
            new { slug, name = $"Qualification {slug}", environment = "production" });
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.Created);
        Guid id = body.GetProperty("id").GetGuid();

        using HttpResponseMessage apiKey = await PostAdminAsync(
            $"/admin/tenants/{id}/api-keys",
            new { name = "qualification-ingress" });
        JsonElement apiKeyBody = await AssertJsonAsync(apiKey, HttpStatusCode.Created);
        Assert.False(apiKeyBody.GetProperty("api_key").TryGetProperty("scopes", out _));
        return new TenantContext(
            id,
            slug,
            apiKeyBody.GetProperty("api_key").GetProperty("id").GetGuid(),
            apiKeyBody.GetProperty("token").GetString()!);
    }

    private async Task<Guid> CreateConnectionAsync(
        TenantContext tenant,
        string integrationId,
        string name,
        string url,
        object? auth = null)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/connections",
            ConnectionBody(integrationId, name, url, auth));
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateTopicAsync(TenantContext tenant, string name, IReadOnlyList<Guid> sources)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics",
            new { name, source_connection_ids = sources });
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
        Guid source = await CreateConnectionAsync(
            tenant, HttpIntegrationId, $"{topicName}-source", $"http://mocksink:8080/sink/{topicName}-source");
        Guid topic = await CreateTopicAsync(tenant, topicName, [source]);
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
            tenant, ApiKeyIntegrationId, $"{sink}-destination", $"http://mocksink:8080/sink/{sink}", ApiKeyAuth(secretReference));
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
            tenant, ApiKeyIntegrationId, $"{sink}-destination", $"http://mocksink:8080/sink/{sink}", ApiKeyAuth(secretReference));
        Guid subscription = await CreateSubscriptionAsync(
            tenant, topic, $"{sink}-subscription", destination, $"{sink}.test");
        EventAcceptance accepted = await IngestAsync(
            tenant, source, sink, $"{sink}.test", new { sink });
        await WaitForAttemptCountAsync(accepted.Id, subscription, 1);
        Assert.Equal(expectedPhase, await fixture.ScalarAsync<string>(
            $"SELECT da.failure_phase FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{accepted.Id}' AND sd.subscription_id = '{subscription}' ORDER BY da.attempt_number LIMIT 1"));
        Assert.Equal(0, await ReceiptCountAsync(sink));
    }

    private async Task<EventAcceptance> IngestAsync(
        TenantContext tenant,
        Guid sourceConnectionId,
        string topicName,
        string eventType,
        object payload,
        string? idempotencyKey = null)
    {
        using HttpResponseMessage response = await SendIngressAsync(
            tenant,
            HttpMethod.Post,
            "/events",
            new
            {
                source_connection_id = sourceConnectionId,
                topic_name = topicName,
                event_type = eventType,
                payload,
                idempotency_key = idempotencyKey ?? $"qualification-{Guid.NewGuid():N}"
            });
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.Accepted);
        return new EventAcceptance(
            body.GetProperty("event_id").GetGuid(),
            body.GetProperty("already_accepted").GetBoolean());
    }

    private async Task AssertAcceptanceRejectedAsync(TenantContext tenant, Guid sourceConnectionId, string topicName)
    {
        using HttpResponseMessage response = await SendIngressAsync(
            tenant,
            HttpMethod.Post,
            "/events",
            new
            {
                source_connection_id = sourceConnectionId,
                topic_name = topicName,
                event_type = "rejected.test",
                payload = new { rejected = true },
                idempotency_key = $"qualification-{Guid.NewGuid():N}"
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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

    private async Task<HttpResponseMessage> SendIngressAsync(
        TenantContext tenant,
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {tenant.ApiToken}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await fixture.IngressClient.SendAsync(request);
    }

    private static async Task<JsonElement> AssertJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task WaitForDeliveryStatusAsync(Guid eventId, Guid subscriptionId, string expected) =>
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM subscription_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'") == expected);

    private async Task WaitForAttemptCountAsync(Guid eventId, Guid subscriptionId, int minimum) =>
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id WHERE sd.event_id = '{eventId}' AND sd.subscription_id = '{subscriptionId}' AND da.status <> 'in_progress'") >= minimum);

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
        throw new TimeoutException($"Qualification evidence was not ready within {EvidenceTimeout}. {lastException?.Message}");
    }

    private async Task AssertReceiptHeaderAsync(string sink, string name, string value)
    {
        using HttpResponseMessage assertion = await fixture.MockSinkClient.PostAsJsonAsync(
            $"/receipts/{sink}/assert-headers",
            new { headers = new Dictionary<string, string> { [name] = value } });
        string body = await assertion.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, assertion.StatusCode);
        Assert.DoesNotContain(value, body, StringComparison.Ordinal);
    }

    private async Task AssertReceiptBodyAsync(string sink, string expected)
    {
        using JsonDocument receipts = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>($"/receipts/{sink}")
            ?? throw new InvalidOperationException("MockSink returned no receipt evidence.");
        Assert.Contains(
            receipts.RootElement.GetProperty("receipts").EnumerateArray(),
            receipt => JsonEquivalent(receipt.GetProperty("body").GetString()!, expected));
    }

    private async Task AssertNoReceiptBodyContainsAsync(string sink, string fragment)
    {
        using JsonDocument receipts = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>($"/receipts/{sink}")
            ?? throw new InvalidOperationException("MockSink returned no receipt evidence.");
        Assert.DoesNotContain(
            receipts.RootElement.GetProperty("receipts").EnumerateArray(),
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

    private static object ConnectionBody(string integrationId, string name, string url, object? auth = null) => new
    {
        integration_id = integrationId,
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
        transform,
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

    private sealed record TenantContext(Guid Id, string Slug, Guid ApiKeyId, string ApiToken);
    private sealed record EventAcceptance(Guid Id, bool AlreadyAccepted);
}
