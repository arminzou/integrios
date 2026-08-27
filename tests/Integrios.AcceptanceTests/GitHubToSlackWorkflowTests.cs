using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Integrios.AcceptanceTests;

// i7a.10: proves the packaged golden path end to end against real images, migrations, and
// deployment configuration, extending the existing acceptance harness and WireMock rather than
// standing up a second end-to-end framework. GitHub and Slack are simulated by a realistically
// signed request and a provider-capable WireMock response respectively; no live provider is
// contacted.
[Collection(PackagedDeploymentCollection.Name)]
public sealed class GitHubToSlackWorkflowTests(PackagedDeploymentFixture fixture)
{
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const string GitHubSecretReference = "github_webhook_secret";
    private const string SlackSecretReference = "slack_bot_token";

    [Fact]
    public async Task PackagedSystem_ProvesGitHubToSlackGoldenPath()
    {
        string githubConnectorId = (await fixture.ApplyExampleManifestAsync("github")).ToString();
        string slackConnectorId = (await fixture.ApplyExampleManifestAsync("slack")).ToString();

        string tenantSlug = $"golden-{Suffix()}";
        Guid tenant = await CreateTenantAsync(tenantSlug);

        const string githubSecret = "acceptance-github-secret";
        await fixture.WriteSourceSecretAsync(tenantSlug, GitHubSecretReference, githubSecret);
        Guid githubConnection = await CreateGitHubConnectionAsync(tenant, githubConnectorId);
        Guid topic = await CreateTopicAsync(tenant, "github-events");
        string callbackPath = await CreateWebhookSourceAsync(tenant, githubConnection, topic);

        await fixture.WriteSecretAsync(tenantSlug, SlackSecretReference, "xoxb-acceptance-token");
        Guid slackConnection = await CreateSlackConnectionAsync(tenant, slackConnectorId);
        Guid subscription = await CreateSlackSubscriptionAsync(tenant, topic, slackConnection);

        // Scenario 1: Slack's ok:true confirms a real success, traversing Event, Topic,
        // Subscription, EventDelivery, and DeliveryAttempt from one realistically signed
        // GitHub request.
        await SetSlackModeAsync(new { ok = true });
        Guid succeeded = await SendSignedPushAsync(callbackPath, githubSecret);
        await WaitForDeliveryStatusAsync(succeeded, subscription, "succeeded");
        await AssertSlackReceivedTransformedMessageAsync();

        // Scenario 2: an HTTP 200 Slack logically rejects is a terminal failure — one attempt, an
        // immediate dead-letter, not three attempts over the retry budget.
        await ResetSlackReceiptsAsync();
        await SetSlackModeAsync(new { ok = false, error = "channel_not_found" });
        Guid rejected = await SendSignedPushAsync(callbackPath, githubSecret);
        await WaitForDeliveryStatusAsync(rejected, subscription, "dead_lettered");
        (await AttemptCountAsync(rejected, subscription)).ShouldBe(1L);

        // Scenario 3: a bounded Retry-After on 429 is honored over the default 30s exponential
        // backoff. If it were ignored, deliver_after would land ~30s after completion instead of
        // ~2s, so a generous 10s bound still distinguishes the two unambiguously.
        await ResetSlackReceiptsAsync();
        await SetSlackModeAsync(body: null, statusCode: 429, retryAfterSeconds: 2);
        Guid throttled = await SendSignedPushAsync(callbackPath, githubSecret);
        await WaitForAttemptCountAsync(throttled, subscription, 1);
        (DateTimeOffset deliverAfter, DateTimeOffset completedAt) = await ReadRetryTimingAsync(throttled, subscription);
        (deliverAfter <= completedAt.AddSeconds(10)).ShouldBeTrue(
            $"Expected the 2s Retry-After to be honored instead of the 30s default backoff; "
            + $"deliver_after={deliverAfter:o}, completed_at={completedAt:o}");

        await SetSlackModeAsync(new { ok = true });
        await WaitForDeliveryStatusAsync(throttled, subscription, "succeeded");
    }

    private async Task<Guid> CreateTenantAsync(string slug)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            "/admin/tenants", new { slug, name = $"Acceptance {slug}", environment = "production" });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateGitHubConnectionAsync(Guid tenant, string connectorId)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/connections",
            new
            {
                connector_id = connectorId,
                name = "github-source",
                config = new { },
                source_verification = new
                {
                    scheme = "hmac_sha256",
                    config = new { },
                    secret_refs = new { secret = GitHubSecretReference },
                },
                environment = "production",
            });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateSlackConnectionAsync(Guid tenant, string connectorId)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/connections",
            new
            {
                connector_id = connectorId,
                name = "slack-destination",
                config = new { base_uri = "http://mocksink:8080/sink/slack" },
                destination_authentication = new
                {
                    scheme = "bearer_token",
                    config = new { },
                    secret_refs = new { token = SlackSecretReference },
                },
                environment = "production",
            });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateTopicAsync(Guid tenant, string name)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/topics", new { name });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<string> CreateWebhookSourceAsync(Guid tenant, Guid connection, Guid topic)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/sources",
            new { connection_id = connection, topic_id = topic, type = "webhook", configuration = new { source_contract = "verified_webhook" } });
        JsonElement source = await AssertJsonAsync(response, HttpStatusCode.Created);
        return $"/webhooks/{source.GetProperty("configuration").GetProperty("callback_id").GetString()}";
    }

    private async Task<Guid> CreateSlackSubscriptionAsync(Guid tenant, Guid topic, Guid destination)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/topics/{topic}/subscriptions",
            new
            {
                name = "push-to-slack",
                match_rules = new { event_type = "github.push" },
                destination_connection_id = destination,
                order_index = 0,
                mapping = new
                {
                    engine = "jsonata",
                    version = "1",
                    expression = "{'channel': '#deploys', 'text': pusher.name & ' pushed to ' & repository.full_name}",
                },
                http_delivery = new { version = 1, method = "POST", headers = new { }, body = "json" },
            });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> SendSignedPushAsync(string callbackPath, string secret)
    {
        string payload = """
            {
              "pusher": { "name": "octocat" },
              "repository": { "full_name": "acme/widgets" },
              "head_commit": { "message": "fix: correct off-by-one in retry backoff" }
            }
            """;
        string signature = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpRequestMessage request = new(HttpMethod.Post, callbackPath) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", $"delivery-{Suffix()}");
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", "push");

        using HttpResponseMessage response = await fixture.IngestionClient.SendAsync(request);
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.Accepted);
        return body.GetProperty("event_id").GetGuid();
    }

    private async Task SetSlackModeAsync(object? body, int? statusCode = null, int? retryAfterSeconds = null)
        => await fixture.WireMockSink.ConfigureAsync("slack", "succeed", statusCode: statusCode, body: body, retryAfterSeconds: retryAfterSeconds);

    private async Task ResetSlackReceiptsAsync()
        => await fixture.WireMockSink.ResetReceiptsAsync("slack");

    private async Task AssertSlackReceivedTransformedMessageAsync()
        => await fixture.WireMockSink.AssertReceiptBodyContainsAsync("slack", "octocat pushed to acme/widgets");

    private async Task<long> AttemptCountAsync(Guid eventId, Guid subscriptionId) =>
        await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts da "
            + "JOIN event_deliveries sd ON sd.id = da.event_delivery_id "
            + $"WHERE sd.event_id = '{eventId}' AND sd.subscription_id = '{subscriptionId}' AND da.status <> 'in_progress'");

    private async Task<(DateTime DeliverAfter, DateTime CompletedAt)> ReadRetryTimingAsync(
        Guid eventId, Guid subscriptionId)
    {
        DateTime deliverAfter = await fixture.ScalarAsync<DateTime>(
            "SELECT deliver_after FROM event_deliveries "
            + $"WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'");
        DateTime completedAt = await fixture.ScalarAsync<DateTime>(
            "SELECT completed_at FROM delivery_attempts WHERE event_delivery_id = "
            + $"(SELECT id FROM event_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}') "
            + "ORDER BY attempt_number DESC LIMIT 1");
        return (deliverAfter, completedAt);
    }

    private async Task WaitForDeliveryStatusAsync(Guid eventId, Guid subscriptionId, string expected) =>
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM event_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'")
            == expected);

    private async Task WaitForAttemptCountAsync(Guid eventId, Guid subscriptionId, int minimum) =>
        await WaitForAsync(async () => await AttemptCountAsync(eventId, subscriptionId) >= minimum);

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

    private async Task<HttpResponseMessage> PostAdminAsync(string path, object body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        return await fixture.AdminClient.SendAsync(request);
    }

    private static async Task<JsonElement> AssertJsonAsync(HttpResponseMessage response, params HttpStatusCode[] expected)
    {
        string body = await response.Content.ReadAsStringAsync();
        expected.Contains(response.StatusCode).ShouldBeTrue(
            $"Expected one of [{string.Join(", ", expected)}], got {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

}
