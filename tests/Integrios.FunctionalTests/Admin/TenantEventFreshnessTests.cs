using System.Data.Common;
using System.Net;
using System.Text.Json;
using Dapper;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class TenantEventFreshnessTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private HttpClient client = null!;
    private Guid sourceId;
    private Guid topicId;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (sourceId, topicId) = await CreateOriginAsync(fixture.TenantId);
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EventType_MatchesStoredTypesExactlyIgnoringCase_EvenWhenNoSourceDeclaresThemAnyMore()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid mixedCase = await InsertEventAsync("Order.Created", "routed", now.AddMinutes(-3));
        Guid lowerCase = await InsertEventAsync("order.created", "routed", now.AddMinutes(-2));
        await InsertEventAsync("order.created.v2", "routed", now.AddMinutes(-1));
        // Not declared by the Source on this Topic; history still finds it.
        Guid retired = await InsertEventAsync("order.retired", "unrouted", now);

        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?event_type=ORDER.CREATED"))
            .ShouldBe([lowerCase, mixedCase]);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?event_type=order.retired")).ShouldBe([retired]);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?event_type=order")).ShouldBeEmpty();
    }

    [Fact]
    public async Task FirstPage_CarriesAWatermark_AndLaterPagesDoNot()
    {
        JsonElement empty = await GetAsync($"/admin/tenants/{fixture.TenantId}/events");
        empty.GetProperty("watermark").GetString().ShouldNotBeNullOrEmpty();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InsertEventAsync("order.created", "routed", now.AddMinutes(-2));
        await InsertEventAsync("order.created", "routed", now.AddMinutes(-1));
        JsonElement first = await GetAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1");
        first.GetProperty("watermark").GetString().ShouldNotBeNullOrEmpty();
        string next = first.GetProperty("next_cursor").GetString()!;

        JsonElement second = await GetAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&after={Uri.EscapeDataString(next)}");
        second.GetProperty("watermark").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Freshness_CountsOnlyLaterEventsMatchingEveryAppliedFilter_AndShowAdvancesTheWatermark()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // The first page shows this Event, so it is the watermark.
        await InsertEventAsync("order.created", "unrouted", now.AddMinutes(-5));
        const string filter = "status=unrouted&event_type=order.created";
        string watermark = await WatermarkAsync(filter);

        DateTimeOffset later = DateTimeOffset.UtcNow.AddSeconds(1);
        await InsertEventAsync("order.created", "unrouted", later);  // matches
        await InsertEventAsync("ORDER.CREATED", "unrouted", later);  // matches, ignoring case
        await InsertEventAsync("order.created", "routed", later);    // other status
        await InsertEventAsync("invoice.sent", "unrouted", later);   // other type
        await InsertEventAsync("order.created", "unrouted", now.AddMinutes(-30)); // accepted before the watermark Event

        JsonElement fresh = await FreshnessAsync(filter, watermark);
        fresh.GetProperty("count").GetInt32().ShouldBe(2);
        fresh.GetProperty("capped").GetBoolean().ShouldBeFalse();

        // Show: the first page is read again and its watermark adopted, so nothing is newer than it.
        string advanced = await WatermarkAsync(filter);
        (await FreshnessAsync(filter, advanced)).GetProperty("count").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Freshness_AfterAnEmptyFirstPage_CountsEveryLaterMatch_EvenOneStampedBeforeTheRead()
    {
        string watermark = await WatermarkAsync("status=unrouted");

        // Stamped before the page was read, committed after it: the reader has still never seen it.
        await InsertEventAsync("order.created", "unrouted", DateTimeOffset.UtcNow.AddMinutes(-10));

        (await FreshnessAsync("status=unrouted", watermark)).GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Freshness_StopsCountingAtItsCap()
    {
        string watermark = await WatermarkAsync("");
        DateTimeOffset at = DateTimeOffset.UtcNow.AddSeconds(1);
        await ExecuteAsync($$"""
            INSERT INTO events (id, tenant_id, source_id, topic_id, event_type, payload, status, accepted_at)
            VALUES (@Id, @TenantId, @SourceId, @TopicId, 'order.created', {{fixture.Json("@Payload")}}, 'routed', @AcceptedAt);
            """,
            Enumerable.Range(0, 101).Select(_ => new { Id = Guid.NewGuid(), fixture.TenantId, SourceId = sourceId, TopicId = topicId, Payload = "{}", AcceptedAt = at }));

        JsonElement fresh = await FreshnessAsync("", watermark);
        fresh.GetProperty("count").GetInt32().ShouldBe(100);
        fresh.GetProperty("capped").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Freshness_RefusesAWatermarkFromAnotherScope()
    {
        await InsertEventAsync("order.created", "routed", DateTimeOffset.UtcNow.AddMinutes(-1));
        await InsertEventAsync("order.created", "routed", DateTimeOffset.UtcNow.AddMinutes(-2));
        JsonElement page = await GetAsync($"/admin/tenants/{fixture.TenantId}/events?status=routed&limit=1");
        string watermark = page.GetProperty("watermark").GetString()!;
        string pageCursor = page.GetProperty("next_cursor").GetString()!;

        // A changed filter starts a new watermark rather than reusing one from another scope.
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events/freshness?status=unrouted&watermark={Uri.EscapeDataString(watermark)}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.OtherTenantId}/events/freshness?status=routed&watermark={Uri.EscapeDataString(watermark)}");
        // A page cursor is not a watermark.
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events/freshness?status=routed&watermark={Uri.EscapeDataString(pageCursor)}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events/freshness?status=routed&watermark=forged");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events/freshness?status=routed");
        // Event type matches ignoring case, so its spelling does not change the scope.
        string typed = await WatermarkAsync("event_type=Order.Created");
        (await FreshnessAsync("event_type=order.created", typed)).GetProperty("count").GetInt32().ShouldBe(0);
    }

    private async Task<string> WatermarkAsync(string filter) =>
        (await GetAsync($"/admin/tenants/{fixture.TenantId}/events?{filter}")).GetProperty("watermark").GetString()!;

    private Task<JsonElement> FreshnessAsync(string filter, string watermark) =>
        GetAsync($"/admin/tenants/{fixture.TenantId}/events/freshness?{filter}&watermark={Uri.EscapeDataString(watermark)}");

    private async Task<(Guid SourceId, Guid TopicId)> CreateOriginAsync(Guid tenantId)
    {
        Guid newTopicId = Guid.NewGuid();
        Guid newSourceId = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO topics (id, tenant_id, {{fixture.KeyColumn}}, name) VALUES (@TopicId, @TenantId, @Name, @Name);
            INSERT INTO sources (id, tenant_id, connector_id, topic_id, name, type, event_types, configuration, revision, status)
            VALUES (@SourceId, @TenantId, @ConnectorId, @TopicId, @Name, 'event_api',
                {{fixture.Json("@EventTypes")}}, {{fixture.Json("@Config")}}, 'fixture-revision', 'active');
            """,
            new { TopicId = newTopicId, SourceId = newSourceId, TenantId = tenantId, ConnectorId = fixture.HttpConnectorId, Name = $"freshness-{newTopicId:N}", EventTypes = "[\"order.created\"]", Config = "{}" });
        return (newSourceId, newTopicId);
    }

    private async Task<Guid> InsertEventAsync(string eventType, string status, DateTimeOffset acceptedAt)
    {
        Guid id = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO events (id, tenant_id, source_id, topic_id, event_type, payload, status, accepted_at)
            VALUES (@Id, @TenantId, @SourceId, @TopicId, @EventType, {{fixture.Json("@Payload")}}, @Status, @AcceptedAt);
            """,
            new { Id = id, fixture.TenantId, SourceId = sourceId, TopicId = topicId, EventType = eventType, Payload = "{}", Status = status, AcceptedAt = acceptedAt });
        return id;
    }

    private async Task AssertBadRequestAsync(string url)
    {
        using HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Get, url));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, url);
    }

    private async Task<IReadOnlyList<Guid>> ListIdsAsync(string url) =>
        (await GetAsync(url)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("event_id").GetGuid()).ToList();

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
