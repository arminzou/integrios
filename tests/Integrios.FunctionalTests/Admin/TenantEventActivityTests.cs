using System.Data.Common;
using System.Net;
using System.Text.Json;
using Dapper;
using Integrios.Application.EventMonitoring;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Events;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Admin;

public sealed class TenantEventActivityTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
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
    public async Task Activity_ClassifiesEveryEventExactlyOnceByItsCurrentOutcome()
    {
        // The seeded Event is routed with one dead-lettered Delivery. A second dead-lettered Delivery
        // on it (borrowing another seeded Event's Subscription) must still count one Event.
        var (deadLetteredEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (otherEventId, otherDeliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();
        await ExecuteAsync("""
            INSERT INTO event_deliveries
                (id, event_id, subscription_id, destination_id, http_execution_snapshot, connector_key,
                 status, lifetime_attempt_count, retry_cycle_attempt_count, failed_at)
            SELECT @Id, @EventId, subscription_id, destination_id, http_execution_snapshot,
                   connector_key, 'dead_lettered', 1, 1, failed_at
            FROM event_deliveries WHERE event_id = @OtherEventId;
            """,
            new { Id = Guid.NewGuid(), EventId = deadLetteredEventId, OtherEventId = otherEventId });
        // Replaying the other Event's Delivery leaves it routed with nothing dead-lettered.
        await ExecuteAsync("UPDATE event_deliveries SET status = 'succeeded', failed_at = NULL WHERE id = @Id", new { Id = otherDeliveryId });

        var (sourceId, topicId) = await EventOriginAsync(deadLetteredEventId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // The window ends on the whole second the read was taken, so keep every Event clear of it.
        await ExecuteAsync("UPDATE events SET accepted_at = @AcceptedAt", new { AcceptedAt = now.AddMinutes(-1) });
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "accepted", now.AddMinutes(-1));
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "unrouted", now.AddMinutes(-1));
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "routed", now.AddMinutes(-1));
        // Outside the hour: never counted.
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "unrouted", now.AddHours(-2));

        JsonElement activity = await GetJsonAsync(client, $"/admin/tenants/{fixture.TenantId}/events/activity?range=1h");
        int Sum(string outcome) => activity.GetProperty("buckets").EnumerateArray().Sum(bucket => bucket.GetProperty(outcome).GetInt32());

        Sum("awaiting_routing").ShouldBe(1);
        Sum("unrouted").ShouldBe(1);
        Sum("delivery_dead_lettered").ShouldBe(1);
        // The replayed Event and the plain routed one.
        Sum("routed").ShouldBe(2);
    }

    [Theory]
    [InlineData("1h", 12, 5 * 60)]
    [InlineData("24h", 24, 60 * 60)]
    [InlineData("7d", 28, 6 * 60 * 60)]
    public async Task Activity_ReturnsOrderedZeroFilledBucketsForEachFixedRange(string range, int count, int bucketSeconds)
    {
        JsonElement activity = await GetJsonAsync(client, $"/admin/tenants/{fixture.TenantId}/events/activity?range={range}");

        activity.GetProperty("range").GetString().ShouldBe(range);
        DateTimeOffset windowStart = activity.GetProperty("window_start").GetDateTimeOffset();
        DateTimeOffset windowEnd = activity.GetProperty("window_end").GetDateTimeOffset();
        windowEnd.Ticks.ShouldBe(windowEnd.Ticks - windowEnd.Ticks % TimeSpan.TicksPerSecond);
        (windowEnd - windowStart).ShouldBe(TimeSpan.FromSeconds(count * bucketSeconds));
        JsonElement[] buckets = activity.GetProperty("buckets").EnumerateArray().ToArray();
        buckets.Length.ShouldBe(count);
        for (int index = 0; index < count; index++)
        {
            buckets[index].GetProperty("start").GetDateTimeOffset().ShouldBe(windowStart.AddSeconds(index * bucketSeconds));
            buckets[index].GetProperty("end").GetDateTimeOffset().ShouldBe(windowStart.AddSeconds((index + 1) * bucketSeconds));
            buckets[index].GetProperty("routed").GetInt32().ShouldBe(0);
        }
    }

    [Fact]
    public async Task Activity_RejectsARangeOutsideTheFixedSet()
    {
        using HttpResponseMessage response = await client.SendAsync(
            AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/events/activity?range=30d"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Activity_BucketsAreHalfOpenAndNeverLeakAcrossTenants()
    {
        var (sourceId, topicId) = await CreateOriginAsync(fixture.TenantId);
        var (otherSourceId, otherTopicId) = await CreateOriginAsync(fixture.OtherTenantId);

        var windowStart = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        TimeSpan bucket = TimeSpan.FromMinutes(5);
        DateTimeOffset windowEnd = windowStart + bucket * 12;
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "routed", windowStart);                             // bucket 0
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "routed", windowStart + bucket - TimeSpan.FromMilliseconds(1)); // bucket 0
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "routed", windowStart + bucket);                    // bucket 1, not 0
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "routed", windowEnd - TimeSpan.FromMilliseconds(1)); // bucket 11
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "routed", windowEnd);                               // after the window
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "routed", windowStart - TimeSpan.FromMilliseconds(1)); // before it
        await InsertEventAsync(fixture.OtherTenantId, otherSourceId, otherTopicId, "routed", windowStart);

        var monitoring = new TenantEventMonitoring(fixture.WebFactory.Services.GetRequiredService<IDbConnectionFactory>());
        IReadOnlyList<EventActivityBucketCounts> counted = await monitoring.GetActivityAsync(
            fixture.TenantId, windowStart, bucket, 12, CancellationToken.None);

        counted.Select(row => (row.BucketIndex, row.Routed)).ShouldBe([(0, 2), (1, 1), (11, 1)]);
    }

    private async Task<(Guid SourceId, Guid TopicId)> EventOriginAsync(Guid eventId)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<(Guid, Guid)>(
            "SELECT source_id, topic_id FROM events WHERE id = @Id", new { Id = eventId });
    }

    private async Task<(Guid SourceId, Guid TopicId)> CreateOriginAsync(Guid tenantId)
    {
        Guid topicId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO topics (id, tenant_id, {{fixture.KeyColumn}}, name) VALUES (@TopicId, @TenantId, @Name, @Name);
            INSERT INTO sources (id, tenant_id, connector_id, topic_id, name, type, event_types, configuration, revision, status)
            VALUES (@SourceId, @TenantId, @ConnectorId, @TopicId, @Name, 'event_api',
                {{fixture.Json("@EventTypes")}}, {{fixture.Json("@Config")}}, 'fixture-revision', 'active');
            """,
            new { TopicId = topicId, SourceId = sourceId, TenantId = tenantId, ConnectorId = fixture.HttpConnectorId, Name = $"activity-{topicId:N}", EventTypes = "[\"activity.test\"]", Config = "{}" });
        return (sourceId, topicId);
    }

    private Task InsertEventAsync(Guid tenantId, Guid sourceId, Guid topicId, string status, DateTimeOffset acceptedAt) =>
        ExecuteAsync($$"""
            INSERT INTO events (id, tenant_id, source_id, topic_id, event_type, payload, status, accepted_at)
            VALUES (@Id, @TenantId, @SourceId, @TopicId, 'activity.test', {{fixture.Json("@Payload")}}, @Status, @AcceptedAt);
            """,
            new { Id = Guid.NewGuid(), TenantId = tenantId, SourceId = sourceId, TopicId = topicId, Payload = "{}", Status = status, AcceptedAt = acceptedAt });

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
