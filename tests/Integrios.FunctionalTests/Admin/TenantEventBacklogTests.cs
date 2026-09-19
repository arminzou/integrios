using System.Data.Common;
using System.Text.Json;
using Dapper;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class TenantEventBacklogTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
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
    public async Task Backlog_IsEmptyWithNoOldestTimestamps_ForAQuietTenant()
    {
        JsonElement backlog = await GetBacklogAsync(fixture.TenantId);

        foreach (string name in new[] { "awaiting_routing", "unrouted", "dead_lettered_deliveries" })
        {
            backlog.GetProperty(name).GetProperty("count").GetInt32().ShouldBe(0);
            backlog.GetProperty(name).GetProperty("oldest_at").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Backlog_CountsAwaitingRoutingHoweverOld_WithTheOldestAcceptance()
    {
        DateTimeOffset now = WholeSeconds(DateTimeOffset.UtcNow);
        var (sourceId, topicId) = await CreateSourceAsync(fixture.TenantId, "order.created");
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "order.created", "accepted", now.AddDays(-3));
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "order.created", "accepted", now.AddMinutes(-1));
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "order.created", "routed", now.AddDays(-4));

        JsonElement awaiting = (await GetBacklogAsync(fixture.TenantId)).GetProperty("awaiting_routing");

        awaiting.GetProperty("count").GetInt32().ShouldBe(2);
        awaiting.GetProperty("oldest_at").GetDateTimeOffset().ShouldBe(now.AddDays(-3));
    }

    [Fact]
    public async Task Backlog_CountsOnlyActionableUnroutedEvents()
    {
        DateTimeOffset now = WholeSeconds(DateTimeOffset.UtcNow);
        var (sourceId, topicId) = await CreateSourceAsync(fixture.TenantId, "order.created");

        // Actionable: the live Source still declares the type, matched ignoring case, however old.
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "Order.Created", "unrouted", now.AddDays(-2));
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "order.created", "unrouted", now);

        // Historical-only: no Source on the Topic declares this type any more.
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "order.retired", "unrouted", now.AddDays(-5));

        // Historical-only: the one Source declaring the type has been deleted.
        var (deletedSourceId, deletedSourceTopicId) = await CreateSourceAsync(fixture.TenantId, "invoice.sent");
        await InsertEventAsync(fixture.TenantId, deletedSourceId, deletedSourceTopicId, "invoice.sent", "unrouted", now.AddDays(-6));
        await ExecuteAsync($"UPDATE sources SET deleted_at = {fixture.Now} WHERE id = @Id", new { Id = deletedSourceId });

        // Historical-only: the Topic itself has been deleted, even though its Source row still declares the type.
        var (orphanSourceId, deletedTopicId) = await CreateSourceAsync(fixture.TenantId, "shipment.sent");
        await InsertEventAsync(fixture.TenantId, orphanSourceId, deletedTopicId, "shipment.sent", "unrouted", now.AddDays(-7));
        await ExecuteAsync($"UPDATE topics SET deleted_at = {fixture.Now} WHERE id = @Id", new { Id = deletedTopicId });

        JsonElement unrouted = (await GetBacklogAsync(fixture.TenantId)).GetProperty("unrouted");

        unrouted.GetProperty("count").GetInt32().ShouldBe(2);
        unrouted.GetProperty("oldest_at").GetDateTimeOffset().ShouldBe(now.AddDays(-2));

        // Historical-only Events are still Event history, only not backlog.
        JsonElement history = await GetJsonAsync(client, $"/admin/tenants/{fixture.TenantId}/events?status=unrouted");
        history.GetProperty("items").GetArrayLength().ShouldBe(5);
    }

    [Fact]
    public async Task Backlog_CountsEveryDeadLetteredDelivery_WithTheOldestFailure()
    {
        DateTimeOffset longAgo = WholeSeconds(DateTimeOffset.UtcNow.AddDays(-9));
        var (_, olderDeliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();
        await fixture.SeedDeadLetteredDeliveryAsync();
        await ExecuteAsync("UPDATE event_deliveries SET failed_at = @FailedAt WHERE id = @Id",
            new { FailedAt = longAgo, Id = olderDeliveryId });

        JsonElement dead = (await GetBacklogAsync(fixture.TenantId)).GetProperty("dead_lettered_deliveries");

        dead.GetProperty("count").GetInt32().ShouldBe(2);
        dead.GetProperty("oldest_at").GetDateTimeOffset().ShouldBe(longAgo);
    }

    [Fact]
    public async Task Backlog_NeverLeaksAcrossTenants()
    {
        DateTimeOffset now = WholeSeconds(DateTimeOffset.UtcNow);
        await fixture.SeedDeadLetteredDeliveryAsync();
        var (sourceId, topicId) = await CreateSourceAsync(fixture.TenantId, "order.created");
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "order.created", "accepted", now);
        await InsertEventAsync(fixture.TenantId, sourceId, topicId, "order.created", "unrouted", now);
        var (otherSourceId, otherTopicId) = await CreateSourceAsync(fixture.OtherTenantId, "order.created");
        await InsertEventAsync(fixture.OtherTenantId, otherSourceId, otherTopicId, "order.created", "unrouted", now);

        JsonElement mine = await GetBacklogAsync(fixture.TenantId);
        mine.GetProperty("awaiting_routing").GetProperty("count").GetInt32().ShouldBe(1);
        mine.GetProperty("unrouted").GetProperty("count").GetInt32().ShouldBe(1);
        mine.GetProperty("dead_lettered_deliveries").GetProperty("count").GetInt32().ShouldBe(1);

        JsonElement other = await GetBacklogAsync(fixture.OtherTenantId);
        other.GetProperty("awaiting_routing").GetProperty("count").GetInt32().ShouldBe(0);
        other.GetProperty("unrouted").GetProperty("count").GetInt32().ShouldBe(1);
        other.GetProperty("dead_lettered_deliveries").GetProperty("count").GetInt32().ShouldBe(0);
    }

    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private Task<JsonElement> GetBacklogAsync(Guid tenantId) => GetJsonAsync(client, $"/admin/tenants/{tenantId}/events/backlog");

    private async Task<(Guid SourceId, Guid TopicId)> CreateSourceAsync(Guid tenantId, params string[] eventTypes)
    {
        Guid topicId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO topics (id, tenant_id, {{fixture.KeyColumn}}, name)
            VALUES (@TopicId, @TenantId, @TopicName, @TopicName);
            INSERT INTO sources (id, tenant_id, connector_id, topic_id, name, type, event_types, configuration, revision, status)
            VALUES (@SourceId, @TenantId, @ConnectorId, @TopicId, @SourceName, 'event_api',
                {{fixture.Json("@EventTypes")}}, {{fixture.Json("@Config")}}, 'fixture-revision', 'active');
            """,
            new
            {
                TopicId = topicId,
                SourceId = sourceId,
                TenantId = tenantId,
                ConnectorId = fixture.HttpConnectorId,
                TopicName = $"backlog-topic-{topicId:N}",
                SourceName = $"backlog-source-{sourceId:N}",
                EventTypes = JsonSerializer.Serialize(eventTypes),
                Config = "{}",
            });
        return (sourceId, topicId);
    }

    private Task InsertEventAsync(Guid tenantId, Guid sourceId, Guid topicId, string eventType, string status, DateTimeOffset acceptedAt) =>
        ExecuteAsync($$"""
            INSERT INTO events (id, tenant_id, source_id, topic_id, event_type, payload, status, accepted_at)
            VALUES (@Id, @TenantId, @SourceId, @TopicId, @EventType, {{fixture.Json("@Payload")}}, @Status, @AcceptedAt);
            """,
            new { Id = Guid.NewGuid(), TenantId = tenantId, SourceId = sourceId, TopicId = topicId, EventType = eventType, Payload = "{}", Status = status, AcceptedAt = acceptedAt });

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
