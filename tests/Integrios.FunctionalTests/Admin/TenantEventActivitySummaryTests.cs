using System.Data.Common;
using System.Text.Json;
using Dapper;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class TenantEventActivitySummaryTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Summary_CountsEventsAndDeadLetteredDeliveriesSeparatelyWithinTheWindow()
    {
        // The seeded Event is routed with one dead-lettered delivery; a second delivery on the same
        // Event (borrowing another seeded Event's Subscription, since one Event cannot have two
        // Deliveries against the same Subscription) proves fanout cannot duplicate the Event count
        // while still doubling the Delivery count.
        var (routedEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (otherEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        JsonElement all = await GetAsync($"/admin/tenants/{fixture.TenantId}/events");
        JsonElement routed = all.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("event_id").GetGuid() == routedEventId);
        Guid sourceId = routed.GetProperty("source_id").GetGuid();
        Guid topicId = routed.GetProperty("topic_id").GetGuid();

        Guid secondDeliveryId = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO event_deliveries
                (id, event_id, subscription_id, destination_connection_id, http_execution_snapshot, connector_key,
                 status, lifetime_attempt_count, retry_cycle_attempt_count, failed_at)
            SELECT @SecondDeliveryId, @RoutedEventId, subscription_id, destination_connection_id, http_execution_snapshot,
                   connector_key, 'dead_lettered', 1, 1, failed_at
            FROM event_deliveries WHERE event_id = @OtherEventId;
            """,
            new { SecondDeliveryId = secondDeliveryId, RoutedEventId = routedEventId, OtherEventId = otherEventId });

        // otherEventId itself must also be counted: it is a second routed Event in the window.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InsertEventAsync(sourceId, topicId, "accepted", now);
        await InsertEventAsync(sourceId, topicId, "unrouted", now);
        // Outside the 60-minute window: must not appear in any count.
        await InsertEventAsync(sourceId, topicId, "unrouted", now.AddHours(-2));

        // Window total: routedEventId, otherEventId, the accepted Event, and the unrouted Event
        // (the one outside the window is excluded).
        JsonElement summary = await GetAsync($"/admin/tenants/{fixture.TenantId}/events/activity-summary");
        summary.GetProperty("events_accepted").GetInt32().ShouldBe(4);
        summary.GetProperty("awaiting_routing").GetInt32().ShouldBe(1);
        summary.GetProperty("unrouted").GetInt32().ShouldBe(1);
        // routedEventId's own delivery, otherEventId's own delivery, and the borrowed second delivery.
        summary.GetProperty("dead_lettered_deliveries").GetInt32().ShouldBe(3);

        DateTimeOffset windowStart = summary.GetProperty("window_start").GetDateTimeOffset();
        DateTimeOffset windowEnd = summary.GetProperty("window_end").GetDateTimeOffset();
        (windowEnd - windowStart).ShouldBe(TimeSpan.FromMinutes(60));
        (DateTimeOffset.UtcNow - windowEnd).ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Summary_ScopesToSourceAndTopicAndNeverLeaksAcrossTenants()
    {
        var (firstEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (secondEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        JsonElement all = await GetAsync($"/admin/tenants/{fixture.TenantId}/events");
        JsonElement first = all.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("event_id").GetGuid() == firstEventId);
        JsonElement second = all.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("event_id").GetGuid() == secondEventId);
        Guid firstSourceId = first.GetProperty("source_id").GetGuid();
        Guid firstTopicId = first.GetProperty("topic_id").GetGuid();
        Guid secondTopicId = second.GetProperty("topic_id").GetGuid();

        JsonElement scoped = await GetAsync(
            $"/admin/tenants/{fixture.TenantId}/events/activity-summary?source_id={firstSourceId}&topic_id={firstTopicId}");
        scoped.GetProperty("events_accepted").GetInt32().ShouldBe(1);
        scoped.GetProperty("dead_lettered_deliveries").GetInt32().ShouldBe(1);

        // Each seeded Event owns its own Source and Topic, so crossing them must match nothing.
        JsonElement crossed = await GetAsync(
            $"/admin/tenants/{fixture.TenantId}/events/activity-summary?source_id={firstSourceId}&topic_id={secondTopicId}");
        crossed.GetProperty("events_accepted").GetInt32().ShouldBe(0);
        crossed.GetProperty("dead_lettered_deliveries").GetInt32().ShouldBe(0);

        JsonElement otherTenant = await GetAsync($"/admin/tenants/{fixture.OtherTenantId}/events/activity-summary");
        otherTenant.GetProperty("events_accepted").GetInt32().ShouldBe(0);
        otherTenant.GetProperty("awaiting_routing").GetInt32().ShouldBe(0);
        otherTenant.GetProperty("unrouted").GetInt32().ShouldBe(0);
        otherTenant.GetProperty("dead_lettered_deliveries").GetInt32().ShouldBe(0);
    }

    private async Task InsertEventAsync(Guid sourceId, Guid topicId, string status, DateTimeOffset acceptedAt) =>
        await ExecuteAsync($$"""
            INSERT INTO events (id, tenant_id, source_id, topic_id, event_type, payload, status, accepted_at)
            VALUES (@Id, @TenantId, @SourceId, @TopicId, 'activity.test', {{fixture.Json("@Payload")}}, @Status, @AcceptedAt);
            """,
            new { Id = Guid.NewGuid(), fixture.TenantId, SourceId = sourceId, TopicId = topicId, Payload = "{}", Status = status, AcceptedAt = acceptedAt });

    private async Task<JsonElement> GetAsync(string url)
    {
        using HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Get, url));
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{url} -> {(int)response.StatusCode}: {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
