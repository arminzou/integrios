using System.Data.Common;
using System.Net;
using System.Text.Json;
using Dapper;
using Integrios.Infrastructure.Common.Pagination;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class AdminListContractTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
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
    public async Task Lists_AreNewestFirstAndRejectMalformedOrWrongContractCursors()
    {
        Guid olderId = Guid.NewGuid();
        Guid newerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await ExecuteAsync(
            "INSERT INTO tenants (id, slug, name, status, created_at, updated_at) VALUES (@OlderId, 'older-list-tenant', 'Older list tenant', 'active', @OlderAt, @OlderAt), (@NewerId, 'newer-list-tenant', 'Newer list tenant', 'active', @NewerAt, @NewerAt)",
            new { OlderId = olderId, NewerId = newerId, OlderAt = now.AddMinutes(1), NewerAt = now.AddMinutes(2) });

        JsonElement firstPage = await GetListAsync("/admin/tenants?limit=1");
        firstPage.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(newerId);
        string cursor = firstPage.GetProperty("next_cursor").GetString()!;

        JsonElement secondPage = await GetListAsync($"/admin/tenants?limit=1&after={Uri.EscapeDataString(cursor)}");
        secondPage.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(olderId);

        (await client.SendAsync(AdminRequest(HttpMethod.Get, "/admin/tenants?after=not-a-cursor"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, "/admin/tenants?after="))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/connectors?after={Uri.EscapeDataString(cursor)}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants?status=active&after={Uri.EscapeDataString(cursor)}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // A wrong scope is rejected the same way an expired stamp is, so minting a cursor under
        // this literal only tests expiry for as long as the literal still is the unfiltered scope.
        // The accepted one alongside it is what fails when that stops being true.
        IDataProtectionProvider dataProtection = fixture.WebFactory.Services.GetRequiredService<IDataProtectionProvider>();
        const string unfilteredScope = "tenants:{\"status\":null,\"environment\":null,\"name\":null}";
        string freshCursor = PageCursor.Encode(dataProtection, unfilteredScope, now, newerId, DateTimeOffset.UtcNow);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants?after={Uri.EscapeDataString(freshCursor)}"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        string expiredCursor = PageCursor.Encode(dataProtection, unfilteredScope, now, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-24).AddSeconds(-1));
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants?after={Uri.EscapeDataString(expiredCursor)}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lists_FilterByTheirApprovedFieldsAndOmitDetailConfiguration()
    {
        Guid sourceConnectorId = await fixture.ApplyConnectorManifestAsync(
            "source_list_contract",
            TestConnectorManifest.Create("source_list_contract", "Source list contract", "source", declarativeSourceContract: true));
        Guid topicId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid subscriptionId = Guid.NewGuid();
        Guid revokedKeyId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await ExecuteAsync($$$"""
            UPDATE tenants SET status = 'disabled' WHERE id = @OtherTenantId;
            UPDATE connections SET status = 'disabled' WHERE id = @ConnectionId;
            INSERT INTO tenant_api_keys (id, tenant_id, name, key_prefix, key_hash, status, created_at, revoked_at)
            VALUES (@RevokedKeyId, @TenantId, 'revoked-list-key', 'ik_revoked', 'sha256:test', 'disabled', @Now, @Now);
            INSERT INTO topics (id, tenant_id, name, status, created_at, updated_at)
            VALUES (@TopicId, @TenantId, 'disabled-list-topic', 'disabled', @Now, @Now);
            INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status, created_at, updated_at, revoked_at)
            VALUES (@SourceId, @TenantId, @ConnectionId, @TopicId, 'event_api', {{{fixture.Json("@Configuration")}}}, 'revoked', @Now, @Now, @Now);
            INSERT INTO subscriptions (id, tenant_id, topic_id, name, match_rules, destination_connection_id, order_index, status, created_at, updated_at)
            VALUES (@SubscriptionId, @TenantId, @TopicId, 'disabled-list-subscription', {{{fixture.Json("@MatchRules")}}}, @ConnectionId, 0, 'disabled', @Now, @Now);
            """,
            new
            {
                fixture.OtherTenantId,
                ConnectionId = fixture.SourceConnectionId,
                fixture.TenantId,
                sourceConnectorId,
                TopicId = topicId,
                SourceId = sourceId,
                SubscriptionId = subscriptionId,
                RevokedKeyId = revokedKeyId,
                Now = now,
                Configuration = "{\"source_contract\":\"event_json\"}",
                MatchRules = "{\"event_type\":\"list.contract\"}",
            });

        (await ListIdsAsync("/admin/tenants?status=disabled")).ShouldContain(fixture.OtherTenantId);
        (await ListIdsAsync("/admin/connectors?direction=source")).ShouldContain(sourceConnectorId);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/connections?status=disabled")).ShouldContain(fixture.SourceConnectionId);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/tenant-api-keys?state=revoked")).ShouldContain(revokedKeyId);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/tenant-api-keys?state=3"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, "/admin/tenants?status=0"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, "/admin/connectors?direction=0"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/connections?status=0"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/sources?status=0"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics?status=0"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions?status=0"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/sources?status=revoked&type=event_api")).ShouldContain(sourceId);
        (await GetListAsync($"/admin/tenants/{fixture.TenantId}/sources?status=revoked&type=event_api")).GetProperty("items")[0].GetProperty("type").GetString().ShouldBe("event_api");
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/topics?status=disabled")).ShouldContain(topicId);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions?status=disabled")).ShouldContain(subscriptionId);

        (await GetListAsync("/admin/connectors")).GetProperty("items")[0].TryGetProperty("manifest", out _).ShouldBeFalse();
        (await GetListAsync($"/admin/tenants/{fixture.TenantId}/connections")).GetProperty("items")[0].TryGetProperty("config", out _).ShouldBeFalse();
        (await GetListAsync($"/admin/tenants/{fixture.TenantId}/sources")).GetProperty("items")[0].TryGetProperty("configuration", out _).ShouldBeFalse();
        (await GetListAsync($"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions")).GetProperty("items")[0].TryGetProperty("match_rules", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SubscriptionsByTenant_FilterAndNameTheirTopicAndDestination()
    {
        Guid firstTopic = Guid.NewGuid();
        Guid secondTopic = Guid.NewGuid();
        Guid firstConnection = Guid.NewGuid();
        Guid secondConnection = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid excluded = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await ExecuteAsync($$$"""
            INSERT INTO topics (id, tenant_id, name, status, created_at, updated_at) VALUES
            (@FirstTopic, @TenantId, 'Orders', 'active', @Now, @Now),
            (@SecondTopic, @TenantId, 'Invoices', 'active', @Now, @Now);
            INSERT INTO connections (id, tenant_id, connector_id, name, config, status, created_at, updated_at) VALUES
            (@FirstConnection, @TenantId, @ConnectorId, 'Primary CRM', {{{fixture.Json("@Config")}}}, 'active', @Now, @Now),
            (@SecondConnection, @TenantId, @ConnectorId, 'Archive', {{{fixture.Json("@Config")}}}, 'active', @Now, @Now);
            INSERT INTO subscriptions
                (id, tenant_id, topic_id, name, match_rules, destination_connection_id, order_index, status, created_at, updated_at) VALUES
            (@First, @TenantId, @FirstTopic, 'Send priority orders', {{{fixture.Json("@Rules")}}}, @FirstConnection, 1, 'active', @Now, @Now),
            (@Second, @TenantId, @FirstTopic, 'Archive orders', {{{fixture.Json("@Rules")}}}, @SecondConnection, 2, 'disabled', @Now, @Now),
            (@Excluded, @TenantId, @SecondTopic, 'Send invoices', {{{fixture.Json("@Rules")}}}, @FirstConnection, 3, 'active', @Now, @Now);
            """,
            new
            {
                fixture.TenantId,
                ConnectorId = fixture.HttpConnectorId,
                FirstTopic = firstTopic,
                SecondTopic = secondTopic,
                FirstConnection = firstConnection,
                SecondConnection = secondConnection,
                First = first,
                Second = second,
                Excluded = excluded,
                Now = now,
                Config = "{}",
                Rules = "{}",
            });

        string root = $"/admin/tenants/{fixture.TenantId}/subscriptions";
        JsonElement row = (await GetListAsync($"{root}?name=PRIORITY")).GetProperty("items")[0];
        row.GetProperty("id").GetGuid().ShouldBe(first);
        row.GetProperty("topic_name").GetString().ShouldBe("Orders");
        row.GetProperty("destination_connection_name").GetString().ShouldBe("Primary CRM");
        JsonElement topicRow = (await GetListAsync($"/admin/tenants/{fixture.TenantId}/topics/{firstTopic}/subscriptions"))
            .GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == first);
        topicRow.GetProperty("destination_connection_name").GetString().ShouldBe("Primary CRM");
        topicRow.TryGetProperty("topic_name", out _).ShouldBeFalse();
        (await ListIdsAsync($"{root}?topic_id={firstTopic}&connection_id={secondConnection}&status=disabled")).ShouldBe([second]);
        (await ListIdsAsync($"{root}?topic_id={secondTopic}")).ShouldBe([excluded]);
        (await ListIdsAsync($"/admin/tenants/{fixture.OtherTenantId}/subscriptions?topic_id={firstTopic}")).ShouldBeEmpty();
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"{root}?topic_id=invalid"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"{root}?status=0"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        JsonElement page = await GetListAsync($"{root}?topic_id={firstTopic}&limit=1");
        string cursor = Uri.EscapeDataString(page.GetProperty("next_cursor").GetString()!);
        (await GetListAsync($"{root}?topic_id={firstTopic}&limit=1&after={cursor}"))
            .GetProperty("items").GetArrayLength().ShouldBe(1);
        foreach (string changed in new[] { "", $"topic_id={secondTopic}", $"topic_id={firstTopic}&status=active", $"topic_id={firstTopic}&name=orders" })
            (await client.SendAsync(AdminRequest(HttpMethod.Get, $"{root}?{changed}&after={cursor}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tenants_SearchNameOrSlugAndEnvironment_AndBindEachFilterToCursor()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await ExecuteAsync("""
            INSERT INTO tenants (id, slug, name, environment, status, created_at, updated_at) VALUES
            (@First, 'first-search', 'Needle team', 'production', 'active', @Now, @Now),
            (@Second, 'needle-slug', 'Second team', 'production', 'active', @Now, @Now),
            (@Other, 'other-search', 'Needle elsewhere', 'staging', 'disabled', @Now, @Now)
            """, new { First = first, Second = second, Other = other, Now = now });

        (await ListIdsAsync("/admin/tenants?name=%20NEEDLE%20&environment=%20PRODUCTION%20"))
            .Order().ShouldBe(new[] { first, second }.Order());
        (await ListIdsAsync("/admin/tenants?name=needle&environment=staging&status=active")).ShouldBeEmpty();
        (await ListIdsAsync("/admin/tenants?name=%25")).ShouldBeEmpty();
        string url = "/admin/tenants?name=needle&environment=production&limit=1";
        JsonElement page = await GetListAsync(url);
        string cursor = Uri.EscapeDataString(page.GetProperty("next_cursor").GetString()!);
        JsonElement next = await GetListAsync($"{url}&after={cursor}");
        next.GetProperty("items").GetArrayLength().ShouldBe(1);
        next.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldNotBe(page.GetProperty("items")[0].GetProperty("id").GetGuid());
        foreach (string changed in new[] { "name=team&environment=production", "name=needle&environment=staging", "name=needle", "environment=production" })
            (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants?{changed}&after={cursor}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Literal sentinel text must not share a cursor scope with an absent filter.
        string unfilteredCursor = Uri.EscapeDataString((await GetListAsync("/admin/tenants?limit=1")).GetProperty("next_cursor").GetString()!);
        foreach (string filter in new[] { "name=all", "environment=all" })
            (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants?{filter}&after={unfilteredCursor}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Connections_MatchEnvironmentWithoutCasingAndKeepFreeTextOutOfTheCursorScope()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO connections (id, tenant_id, connector_id, name, config, status, environment) VALUES
            (@First, @TenantId, @ConnectorId, 'Ledger gateway', {{fixture.Json("@Config")}}, 'active', 'Production'),
            (@Second, @TenantId, @ConnectorId, 'Ledger relay', {{fixture.Json("@Config")}}, 'active', 'Production')
            """, new { First = first, Second = second, fixture.TenantId, ConnectorId = fixture.HttpConnectorId,
                Config = "{\"base_uri\":\"http://localhost:5054/sink/source\"}" });

        string root = $"/admin/tenants/{fixture.TenantId}/connections";
        (await ListIdsAsync($"{root}?environment=PRODUCTION&name=LEDGER")).Order().ShouldBe(new[] { first, second }.Order());
        string url = $"{root}?environment=production&name=ledger&limit=1";
        string cursor = Uri.EscapeDataString((await GetListAsync(url)).GetProperty("next_cursor").GetString()!);
        (await GetListAsync($"{url}&after={cursor}")).GetProperty("items").GetArrayLength().ShouldBe(1);

        // Literal sentinel text must not share a cursor scope with an absent filter.
        string unfilteredCursor = Uri.EscapeDataString((await GetListAsync($"{root}?limit=1")).GetProperty("next_cursor").GetString()!);
        foreach (string filter in new[] { "name=all", "environment=all", "connector=all" })
            (await client.SendAsync(AdminRequest(HttpMethod.Get, $"{root}?{filter}&after={unfilteredCursor}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sources_ProjectContract_FilterTopic_AndKeepCursorTenantAndFilterScope()
    {
        Guid topic = Guid.NewGuid();
        Guid otherTopic = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid excluded = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await ExecuteAsync($$"""
            INSERT INTO topics (id, tenant_id, name, status, created_at, updated_at) VALUES
            (@Topic, @TenantId, 'source-filter-topic', 'active', @Now, @Now),
            (@OtherTopic, @TenantId, 'other-source-filter-topic', 'active', @Now, @Now);
            INSERT INTO sources (id, tenant_id, connection_id, topic_id, type, configuration, status, created_at, updated_at, revoked_at) VALUES
            (@First, @TenantId, @ConnectionId, @Topic, 'event_api', {{fixture.Json("@Configuration")}}, 'active', @Now, @Now, NULL),
            (@Second, @TenantId, @ConnectionId, @Topic, 'webhook', {{fixture.Json("@Configuration")}}, 'revoked', @Now, @Now, @Now),
            (@Excluded, @TenantId, @ConnectionId, @OtherTopic, 'queue', {{fixture.Json("@Configuration")}}, 'active', @Now, @Now, NULL)
            """, new { Topic = topic, OtherTopic = otherTopic, First = first, Second = second, Excluded = excluded,
                fixture.TenantId, ConnectionId = fixture.SourceConnectionId, Now = now, Configuration = "{\"source_contract\":\"event_json\",\"private_extra\":\"not a list field\"}" });

        string root = $"/admin/tenants/{fixture.TenantId}/sources";
        (await ListIdsAsync($"{root}?topic_id={topic}")).Order().ShouldBe(new[] { first, second }.Order());
        (await ListIdsAsync($"{root}?topic_id={topic}&type=event_api&status=active")).ShouldBe(new[] { first });
        (await ListIdsAsync($"{root}?topic_id={topic}&type=queue")).ShouldBeEmpty();
        (await ListIdsAsync($"/admin/tenants/{fixture.OtherTenantId}/sources?topic_id={topic}")).ShouldBeEmpty();
        (await ListIdsAsync($"{root}?topic_id={Guid.NewGuid()}")).ShouldBeEmpty();
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"{root}?topic_id=invalid"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        foreach (string type in new[] { "event_api", "webhook", "queue" })
        {
            JsonElement item = (await GetListAsync($"{root}?type={type}")).GetProperty("items")[0];
            item.GetProperty("source_contract").GetString().ShouldBe("event_json");
            item.TryGetProperty("configuration", out _).ShouldBeFalse();
        }
        JsonElement page = await GetListAsync($"{root}?topic_id={topic}&limit=1");
        string cursor = Uri.EscapeDataString(page.GetProperty("next_cursor").GetString()!);
        JsonElement next = await GetListAsync($"{root}?topic_id={topic}&limit=1&after={cursor}");
        next.GetProperty("items").GetArrayLength().ShouldBe(1);
        next.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldNotBe(page.GetProperty("items")[0].GetProperty("id").GetGuid());
        foreach (string changed in new[] { $"topic_id={otherTopic}", "", $"topic_id={topic}&status=active", $"topic_id={topic}&type=event_api" })
            (await client.SendAsync(AdminRequest(HttpMethod.Get, $"{root}?{changed}&after={cursor}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.OtherTenantId}/sources?topic_id={topic}&after={cursor}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<IReadOnlyList<Guid>> ListIdsAsync(string url) =>
        (await GetListAsync(url)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

    private async Task<JsonElement> GetListAsync(string url)
    {
        using HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Get, url));
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
