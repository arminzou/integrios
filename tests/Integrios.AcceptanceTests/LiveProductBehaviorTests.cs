using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Integrios.AcceptanceTests;

[Collection(PackagedDeploymentCollection.Name)]
public sealed class LiveProductBehaviorTests(PackagedDeploymentFixture fixture)
{
    private string HttpConnectorId => fixture.HttpConnectorId.ToString();
    private string ApiKeyConnectorId => fixture.ApiKeyConnectorId.ToString();
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Nothing listens on this port, so an attempt that reaches the HTTP phase proves every secret
    // it needed was resolved and applied; the request itself is refused.
    private const string ClosedPortBaseUri = "http://127.0.0.1:1";

    // Every Fact below arranges its own uniquely named Tenant data and restores the
    // deployment-global state it touches (the Worker's secret provider normalized back to the
    // file default). No Fact depends on another Fact's execution order or on credentials captured
    // before a rotation.

    [Fact]
    public async Task SecretSources_ResolveRotateAndValidate_WithoutDisclosingValues()
    {
        TenantContext fileA = await CreateTenantAsync($"file-a-{Suffix()}");
        TenantContext fileB = await CreateTenantAsync($"file-b-{Suffix()}");
        TenantContext rotation = await CreateTenantAsync($"rotation-{Suffix()}");
        // The environment-variable scenario is wired to this exact slug by the acceptance Compose
        // environment, so it must not be suffixed.
        TenantContext configuration = await CreateTenantAsync("acceptance-config");
        await fixture.WriteDestinationSecretAsync(fileA.Slug, "shared-secret", "file-a-value");
        await fixture.WriteDestinationSecretAsync(configuration.Slug, "shared-secret", "file-value");
        await fixture.WriteDestinationSecretAsync(configuration.Slug, "line-break", "unsafe\r\nvalue");
        await fixture.RecreateWorkerAsync("configuration-value", "configuration-only-value");

        // Key-per-file files resolve per Tenant; the same reference never crosses Tenants.
        (await FirstAttemptPhaseAsync(fileA, "file-a", "shared-secret")).ShouldBe("http");
        (await FirstAttemptPhaseAsync(fileB, "file-b", "shared-secret")).ShouldBe("secret_resolution");

        // An environment variable resolves on its own, alongside a key-per-file file.
        (await FirstAttemptPhaseAsync(configuration, "configuration-only", "config-only")).ShouldBe("http");
        (await FirstAttemptPhaseAsync(configuration, "configuration-file", "shared-secret")).ShouldBe("http");
        (await FirstAttemptPhaseAsync(configuration, "configuration-missing", "absent")).ShouldBe("secret_resolution");
        (await FirstAttemptPhaseAsync(configuration, "line-break", "line-break")).ShouldBe("request_construction");

        // Rotation is a file write followed by a restart; a pending retry picks up the new value.
        (Guid rotationEvent, Guid rotationSubscription) = await DeliverWithApiKeyAsync(rotation, "rotation", "shared-secret");
        (await WaitForFirstAttemptPhaseAsync(rotationEvent, rotationSubscription)).ShouldBe("secret_resolution");
        await fixture.WriteDestinationSecretAsync(rotation.Slug, "shared-secret", "rotation-v2");
        await fixture.RestartWorkerAsync();
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{rotationEvent}' AND sd.subscription_id = '{rotationSubscription}' AND da.failure_phase = 'http'") >= 1);

        ComposeResult tenantCli = await fixture.RunWorkerCommandAsync(
            "secrets", "validate", "--tenant", fileA.Slug);
        tenantCli.ExitCode.ShouldBe(0);
        tenantCli.Output.ShouldNotContain("file-a-value", Case.Sensitive);

        ComposeResult unresolvedTenantCli = await fixture.RunWorkerCommandAsync(
            "secrets", "validate", "--tenant", fileB.Slug);
        unresolvedTenantCli.ExitCode.ShouldBe(1);

        ComposeResult fullDeploymentCli = await fixture.RunWorkerCommandAsync(
            "secrets", "validate", "--all");
        fullDeploymentCli.ExitCode.ShouldBe(1);
        fullDeploymentCli.Output.ShouldNotContain("file-a-value", Case.Sensitive);
        fullDeploymentCli.Output.ShouldNotContain("rotation-v2", Case.Sensitive);
        fullDeploymentCli.Output.ShouldNotContain("configuration-only-value", Case.Sensitive);
    }

    [Fact]
    public async Task BootstrapAndOperatorKeyRotation_AreIdempotentAndRestartSafe()
    {
        long operatorKeysBefore = await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM operator_keys");
        await fixture.RunSetupAgainAsync();
        (await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM operator_keys")).ShouldBe(operatorKeysBefore);
        (await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM connectors WHERE key = 'http'")).ShouldBe(1L);

        await fixture.RestartProductServicesAsync();
        using HttpResponseMessage healthyAdmin = await fixture.AdminOperationalClient.GetAsync("/health");
        using HttpResponseMessage healthyIngestion = await fixture.IngestionOperationalClient.GetAsync("/health");
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

    [Fact]
    public async Task Secrets_NeverAppearInDurableEvidence()
    {
        // Both probe secrets are resolved through real traffic first, so their absence from durable
        // evidence is falsifiable rather than vacuous. Outbound is a delivery the Worker
        // authenticates; inbound is a webhook Ingestion verifies. They are separate mounts read by
        // separate processes, so covering only one says nothing about the other.
        TenantContext tenant = await CreateTenantAsync($"secrets-{Suffix()}");
        await fixture.WriteDestinationSecretAsync(tenant.Slug, "probe-secret", "probe-secret-value");
        await fixture.RestartWorkerAsync();
        (await FirstAttemptPhaseAsync(tenant, "probe", "probe-secret")).ShouldBe("http");

        await fixture.WriteSourceSecretAsync(tenant.Slug, "probe-source-secret", "probe-source-secret-value");
        await fixture.RestartIngestionAsync();
        await AssertVerifiedWebhookIntakeAsync(tenant, "probe-source-secret", "probe-source-secret-value");

        (await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM destinations WHERE configuration::text ~ 'probe-secret-value' OR COALESCE(authentication::text, '') ~ 'probe-secret-value'")).ShouldBe(0L);
        (await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM sources WHERE COALESCE(verification::text, '') ~ 'probe-source-secret-value' OR configuration::text ~ 'probe-source-secret-value'")).ShouldBe(0L);
        (await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM delivery_attempts WHERE COALESCE(error_message, '') ~ 'probe-secret-value'")).ShouldBe(0L);

        string logs = string.Join('\n',
            await fixture.GetServiceLogsAsync("admin"),
            await fixture.GetServiceLogsAsync("ingestion"),
            await fixture.GetServiceLogsAsync("worker"));
        logs.ShouldNotContain("probe-secret-value", Case.Sensitive);
        logs.ShouldNotContain("probe-source-secret-value", Case.Sensitive);
    }

    // Drives one signed webhook end to end so the Source-side secret is genuinely resolved by
    // Ingestion. Without this the sources.verification assertion above would pass for a deployment
    // that had never held an inbound secret at all.
    private async Task AssertVerifiedWebhookIntakeAsync(
        TenantContext tenant,
        string secretReference,
        string secretValue)
    {
        Guid topic = await CreateTopicAsync(tenant, $"inbound-{Suffix()}");
        using HttpResponseMessage created = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/sources",
            new
            {
                connector_id = fixture.VerifiedSourceConnectorId.ToString(),
                topic_id = topic,
                name = "verified-intake",
                type = "webhook",
                event_types = new[] { "probe.inbound" },
                configuration = new { },
                verification = new { scheme = "hmac_sha256", config = new { }, secret_refs = new { secret = secretReference } },
                input_requirements = (object?)null,
                mapping = Jsonata("""{ "event_type": "probe.inbound", "payload": $ }"""),
                event_identity_rule = (object?)null,
            });
        JsonElement source = await AssertJsonAsync(created, HttpStatusCode.Created);
        await EnableAsync($"/admin/tenants/{tenant.Id}/sources/{source.GetProperty("id").GetGuid()}");
        string callback = source.GetProperty("configuration").GetProperty("callback_id").GetString()!;

        const string payload = """{"probe":"inbound"}""";
        string signature = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretValue), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpRequestMessage request = new(HttpMethod.Post, $"/webhooks/{callback}") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);

        using HttpResponseMessage accepted = await fixture.IngestionClient.SendAsync(request);
        await AssertJsonAsync(accepted, HttpStatusCode.Accepted);
    }

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
        return new TenantContext(id, slug, tenantApiKeyBody.GetProperty("token").GetString()!);
    }

    private async Task<Guid> CreateTopicAsync(TenantContext tenant, string key)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics",
            new { key });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    // Sources and Subscriptions are authored Inactive; a journey activates each before its traffic.
    private async Task EnableAsync(string path)
    {
        using HttpResponseMessage response = await PostAdminAsync($"{path}/activate", new { });
        (await AssertJsonAsync(response, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("active");
    }

    // One Event through an API-key Destination on the closed port, authenticated by the Tenant's
    // secret reference.
    private async Task<(Guid Event, Guid Subscription)> DeliverWithApiKeyAsync(
        TenantContext tenant,
        string name,
        string secretReference)
    {
        string eventType = $"{name}.test";
        Guid topic = await CreateTopicAsync(tenant, name);

        using HttpResponseMessage sourceResponse = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/sources",
            new { connector_id = HttpConnectorId, topic_id = topic, name = "intake", type = "event_api", event_types = new[] { eventType }, configuration = new { } });
        Guid source = (await AssertJsonAsync(sourceResponse, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await EnableAsync($"/admin/tenants/{tenant.Id}/sources/{source}");

        using HttpResponseMessage destinationResponse = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/destinations",
            new
            {
                connector_id = ApiKeyConnectorId,
                name = $"{name}-destination",
                configuration = new { base_uri = $"{ClosedPortBaseUri}/{name}" },
                authentication = new
                {
                    scheme = "api_key_header",
                    config = new { header_name = "X-Api-Key" },
                    secret_refs = new { api_key = secretReference }
                },
                environment = "production"
            });
        Guid destination = (await AssertJsonAsync(destinationResponse, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using HttpResponseMessage subscriptionResponse = await PostAdminAsync(
            $"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions",
            new { name = $"{name}-subscription", event_types = new[] { eventType }, destination_id = destination });
        Guid subscription = (await AssertJsonAsync(subscriptionResponse, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await EnableAsync($"/admin/tenants/{tenant.Id}/topics/{topic}/subscriptions/{subscription}");

        using HttpResponseMessage ingested = await SendIngestionAsync(
            tenant,
            $"/events?source_id={source}",
            new { event_type = eventType, source_event_id = $"acceptance-{Guid.NewGuid():N}", payload = new { name } });
        Guid eventId = (await AssertJsonAsync(ingested, HttpStatusCode.Accepted)).GetProperty("event_id").GetGuid();
        return (eventId, subscription);
    }

    private async Task<string> FirstAttemptPhaseAsync(TenantContext tenant, string name, string secretReference)
    {
        (Guid eventId, Guid subscription) = await DeliverWithApiKeyAsync(tenant, name, secretReference);
        return await WaitForFirstAttemptPhaseAsync(eventId, subscription);
    }

    private async Task<string> WaitForFirstAttemptPhaseAsync(Guid eventId, Guid subscriptionId)
    {
        string attempts = $"FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}' AND sd.subscription_id = '{subscriptionId}' AND da.status <> 'in_progress'";
        await WaitForAsync(async () => await fixture.ScalarAsync<long>($"SELECT COUNT(*) {attempts}") >= 1);
        return await fixture.ScalarAsync<string>($"SELECT da.failure_phase {attempts} ORDER BY da.attempt_number LIMIT 1");
    }

    private async Task<HttpResponseMessage> PostAdminAsync(string path, object body) =>
        await SendAdminAsync(HttpMethod.Post, path, body);

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await fixture.AdminClient.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendIngestionAsync(TenantContext tenant, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {tenant.ApiToken}");
        return await fixture.IngestionClient.SendAsync(request);
    }

    private static async Task<JsonElement> AssertJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        string body = await response.Content.ReadAsStringAsync();
        (response.StatusCode == expected).ShouldBeTrue($"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

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

    private static object Jsonata(string expression) => new { engine = "jsonata", version = "1", expression };
    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private sealed record TenantContext(Guid Id, string Slug, string ApiToken);
}
