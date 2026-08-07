using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Integrios.AcceptanceTests;

// i7a.10: proves the packaged golden path end to end against real images, migrations, and
// deployment configuration, extending the existing qualification harness and MockSink rather than
// standing up a second end-to-end framework. GitHub and Slack are simulated by a realistically
// signed request and a provider-capable MockSink response respectively; no live provider is
// contacted.
[Collection(PackagedDeploymentCollection.Name)]
[Trait("Category", "Qualification")]
public sealed class GitHubToSlackQualificationTests(PackagedDeploymentFixture fixture)
{
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const string GitHubSecretReference = "github_webhook_secret";
    private const string SlackSecretReference = "slack_bot_token";

    [Fact]
    public async Task PackagedSystem_QualifiesGitHubToSlackGoldenPath()
    {
        string githubIntegrationId = await ApplyExampleManifestAsync("github");
        string slackIntegrationId = await ApplyExampleManifestAsync("slack");

        string tenantSlug = $"golden-{Suffix()}";
        Guid tenant = await CreateTenantAsync(tenantSlug);

        const string githubSecret = "qualification-github-secret";
        await fixture.WriteSourceSecretAsync(tenantSlug, GitHubSecretReference, githubSecret);
        Guid githubConnection = await CreateGitHubConnectionAsync(tenant, githubIntegrationId);
        Guid topic = await CreateTopicAsync(tenant, "github-events", [githubConnection]);
        string callbackPath = await GetCallbackPathAsync(tenant, topic);

        await fixture.WriteSecretAsync(tenantSlug, SlackSecretReference, "xoxb-qualification-token");
        Guid slackConnection = await CreateSlackConnectionAsync(tenant, slackIntegrationId);
        Guid subscription = await CreateSlackSubscriptionAsync(tenant, topic, slackConnection);

        // Scenario 1: Slack's ok:true confirms a real success, traversing Event, Topic,
        // Subscription, SubscriptionDelivery, and DeliveryAttempt from one realistically signed
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
        Assert.Equal(1L, await AttemptCountAsync(rejected, subscription));

        // Scenario 3: a bounded Retry-After on 429 is honored over the default 30s exponential
        // backoff. If it were ignored, deliver_after would land ~30s after completion instead of
        // ~2s, so a generous 10s bound still distinguishes the two unambiguously.
        await ResetSlackReceiptsAsync();
        await SetSlackModeAsync(body: null, statusCode: 429, retryAfterSeconds: 2);
        Guid throttled = await SendSignedPushAsync(callbackPath, githubSecret);
        await WaitForAttemptCountAsync(throttled, subscription, 1);
        (DateTimeOffset deliverAfter, DateTimeOffset completedAt) = await ReadRetryTimingAsync(throttled, subscription);
        Assert.True(
            deliverAfter <= completedAt.AddSeconds(10),
            $"Expected the 2s Retry-After to be honored instead of the 30s default backoff; "
            + $"deliver_after={deliverAfter:o}, completed_at={completedAt:o}");

        await SetSlackModeAsync(new { ok = true });
        await WaitForDeliveryStatusAsync(throttled, subscription, "succeeded");
    }

    private async Task<string> ApplyExampleManifestAsync(string key)
    {
        string path = Path.Combine(RepositoryRoot(), "examples", "integrations", $"{key}-v1.json");
        using var content = new StringContent(await File.ReadAllTextAsync(path), Encoding.UTF8, "application/json");
        using HttpRequestMessage request = new(HttpMethod.Put, $"/admin/integrations/{key}/versions/1") { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        using HttpResponseMessage response = await fixture.AdminClient.SendAsync(request);
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.Created, HttpStatusCode.OK);
        return body.GetProperty("id").GetGuid().ToString();
    }

    private async Task<Guid> CreateTenantAsync(string slug)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            "/admin/tenants", new { slug, name = $"Qualification {slug}", environment = "production" });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateGitHubConnectionAsync(Guid tenant, string integrationId)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/connections",
            new
            {
                integration_id = integrationId,
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

    private async Task<Guid> CreateSlackConnectionAsync(Guid tenant, string integrationId)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/connections",
            new
            {
                integration_id = integrationId,
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

    private async Task<Guid> CreateTopicAsync(Guid tenant, string name, IReadOnlyList<Guid> sources)
    {
        using HttpResponseMessage response = await PostAdminAsync(
            $"/admin/tenants/{tenant}/topics", new { name, source_connection_ids = sources });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
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
                transform = new
                {
                    engine = "jsonata",
                    version = "1",
                    expression = "{'channel': '#deploys', 'text': pusher.name & ' pushed to ' & repository.full_name}",
                },
                http_delivery = new { version = 1, method = "POST", headers = new { }, body = "json" },
            });
        return (await AssertJsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private async Task<string> GetCallbackPathAsync(Guid tenant, Guid topic)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"/admin/tenants/{tenant}/topics/{topic}");
        request.Headers.TryAddWithoutValidation("Authorization", fixture.AdminAuthorization);
        using HttpResponseMessage response = await fixture.AdminClient.SendAsync(request);
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.OK);
        return body.GetProperty("sources")[0].GetProperty("endpoint").GetProperty("callback_path").GetString()!;
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

        using HttpResponseMessage response = await fixture.IngressClient.SendAsync(request);
        JsonElement body = await AssertJsonAsync(response, HttpStatusCode.Accepted);
        return body.GetProperty("event_id").GetGuid();
    }

    private async Task SetSlackModeAsync(object? body, int? statusCode = null, int? retryAfterSeconds = null)
    {
        using HttpResponseMessage response = await fixture.MockSinkClient.PutAsJsonAsync(
            "/control/slack",
            new { mode = "succeed", statusCode, body, retryAfterSeconds });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task ResetSlackReceiptsAsync()
    {
        using HttpResponseMessage response = await fixture.MockSinkClient.DeleteAsync("/receipts/slack");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AssertSlackReceivedTransformedMessageAsync()
    {
        using JsonDocument receipts = await fixture.MockSinkClient.GetFromJsonAsync<JsonDocument>("/receipts/slack")
            ?? throw new InvalidOperationException("MockSink returned no receipt evidence for slack.");
        Assert.Contains(
            receipts.RootElement.GetProperty("receipts").EnumerateArray(),
            receipt => receipt.GetProperty("body").GetString()!.Contains("octocat pushed to acme/widgets", StringComparison.Ordinal));
    }

    private async Task<long> AttemptCountAsync(Guid eventId, Guid subscriptionId) =>
        await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts da "
            + "JOIN subscription_deliveries sd ON sd.id = da.subscription_delivery_id "
            + $"WHERE sd.event_id = '{eventId}' AND sd.subscription_id = '{subscriptionId}' AND da.status <> 'in_progress'");

    private async Task<(DateTime DeliverAfter, DateTime CompletedAt)> ReadRetryTimingAsync(
        Guid eventId, Guid subscriptionId)
    {
        DateTime deliverAfter = await fixture.ScalarAsync<DateTime>(
            "SELECT deliver_after FROM subscription_deliveries "
            + $"WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'");
        DateTime completedAt = await fixture.ScalarAsync<DateTime>(
            "SELECT completed_at FROM delivery_attempts WHERE subscription_delivery_id = "
            + $"(SELECT id FROM subscription_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}') "
            + "ORDER BY attempt_number DESC LIMIT 1");
        return (deliverAfter, completedAt);
    }

    private async Task WaitForDeliveryStatusAsync(Guid eventId, Guid subscriptionId, string expected) =>
        await WaitForAsync(async () =>
            await fixture.ScalarAsync<string>(
                $"SELECT status FROM subscription_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'")
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
        throw new TimeoutException($"Qualification evidence was not ready within {EvidenceTimeout}. {lastException?.Message}");
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
        Assert.True(
            expected.Contains(response.StatusCode),
            $"Expected one of [{string.Join(", ", expected)}], got {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Integrios repository root.");
    }
}
