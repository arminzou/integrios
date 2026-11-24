using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Integrios.AcceptanceTests;

[Collection(PackagedDeploymentCollection.Name)]
public sealed class InterruptionAndConcurrencyTests(PackagedDeploymentFixture fixture)
{
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task PullDelivery_ReleasedBeforePostgresRestart_IsClaimedAgainAndAcked()
    {
        string name = $"pull-{Guid.NewGuid().ToString("N")[..8]}";
        Guid tenantId = await PostAdminForIdAsync(
            "/admin/tenants",
            new { slug = name, name = $"Pull {name}", environment = "production" });
        string apiToken = await PostAdminForPropertyAsync(
            $"/admin/tenants/{tenantId}/tenant-api-keys",
            new { name = "pull-ingestion" },
            "token");
        Guid pullConnectorId = await fixture.ScalarAsync<Guid>("SELECT id FROM connectors WHERE key = 'pull'");
        Guid destinationId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/destinations",
            new { connector_id = pullConnectorId, name = "pull-destination", configuration = new { }, environment = "production" });
        string subscriberToken = await PostAdminForPropertyAsync(
            $"/admin/tenants/{tenantId}/destinations/{destinationId}/subscriber-keys",
            new { name = "pull-subscriber" },
            "token");
        Guid topicId = await PostAdminForIdAsync($"/admin/tenants/{tenantId}/topics", new { key = name });
        Guid sourceId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/sources",
            new { connector_id = fixture.HttpConnectorId, topic_id = topicId, name = "intake", type = "event_api", event_types = new[] { $"{name}.test" }, configuration = new { } });
        await EnableAsync($"/admin/tenants/{tenantId}/sources/{sourceId}");
        Guid subscriptionId = await PostAdminForIdAsync(
            $"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions",
            new { name = "pull-subscription", event_types = new[] { $"{name}.test" }, destination_id = destinationId });
        await EnableAsync($"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{subscriptionId}");

        Guid eventId = await IngestAsync(apiToken, sourceId, $"{name}.test");

        JsonElement first = await ClaimAsync(subscriberToken);
        first.GetProperty("event_id").GetGuid().ShouldBe(eventId);
        (await ReportAsync(subscriberToken, first, "release", new { retry_after_seconds = 0 }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await fixture.RestartPostgresAsync();

        // Restarting the product services is part of the interruption window, not a workaround:
        // pooled connections opened before the restart are dead, and the platform has to come
        // back without operator intervention.
        await fixture.RestartProductServicesAsync();

        JsonElement second = await ClaimAsync(subscriberToken);
        second.GetProperty("delivery_id").GetGuid().ShouldBe(first.GetProperty("delivery_id").GetGuid());
        second.GetProperty("attempt_number").GetInt32().ShouldBe(2);
        (await ReportAsync(subscriberToken, second, "ack")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await fixture.ScalarAsync<string>(
            $"SELECT status FROM event_deliveries WHERE event_id = '{eventId}' AND subscription_id = '{subscriptionId}'")).ShouldBe("succeeded");
        (await fixture.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM delivery_attempts da JOIN event_deliveries sd ON sd.id = da.event_delivery_id WHERE sd.event_id = '{eventId}'")).ShouldBeGreaterThanOrEqualTo(2L);
    }

    // Fan-out and the released delivery's readiness are asynchronous, so claim until one arrives.
    private async Task<JsonElement> ClaimAsync(string subscriberToken)
    {
        JsonElement delivery = default;
        await WaitForAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/delivery/v1/deliveries:claim")
            {
                Content = JsonContent.Create(new { max = 1, wait_seconds = 0 }),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {subscriberToken}");
            using HttpResponseMessage response = await fixture.SubscriberClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return false;
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            delivery = document.RootElement.GetProperty("deliveries").EnumerateArray().ShouldHaveSingleItem().Clone();
            return true;
        });
        return delivery;
    }

    private async Task<HttpResponseMessage> ReportAsync(string subscriberToken, JsonElement delivery, string verb, object? body = null)
    {
        var content = JsonSerializer.SerializeToNode(body ?? new { })!.AsObject();
        content["lease_token"] = delivery.GetProperty("lease_token").GetString();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/delivery/v1/deliveries/{delivery.GetProperty("delivery_id").GetGuid()}:{verb}")
        {
            Content = JsonContent.Create(content),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {subscriberToken}");
        return await fixture.SubscriberClient.SendAsync(request);
    }

    private async Task<Guid> IngestAsync(string apiToken, Guid sourceId, string eventType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={sourceId}")
        {
            Content = JsonContent.Create(new
            {
                event_type = eventType,
                source_event_id = $"pull-{Guid.NewGuid():N}",
                payload = new { restart = true },
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiToken}");
        using HttpResponseMessage response = await fixture.IngestionClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("event_id").GetGuid();
    }

    private async Task<Guid> PostAdminForIdAsync(string path, object body) =>
        Guid.Parse(await PostAdminForPropertyAsync(path, body, "id"));

    // Sources and Subscriptions are authored Disabled; a journey enables each before its traffic.
    private async Task EnableAsync(string path) =>
        (await PostAdminForPropertyAsync($"{path}/activate", new { }, "status")).ShouldBe("active");

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
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Resilience evidence was not ready within {EvidenceTimeout}. {lastException?.Message}");
    }
}
