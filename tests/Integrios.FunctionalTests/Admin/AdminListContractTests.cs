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
        string expiredCursor = PageCursor.Encode(fixture.WebFactory.Services.GetRequiredService<IDataProtectionProvider>(), "tenants:all", now, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-24).AddSeconds(-1));
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
