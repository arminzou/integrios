using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public abstract class SubscriptionAdminTestBase : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    protected readonly AdminApiFixture Fixture;
    protected HttpClient client = null!;

    protected SubscriptionAdminTestBase(AdminApiFixture fixture) => Fixture = fixture;

    public async Task InitializeAsync()
    {
        await Fixture.ResetAsync();
        client = Fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    internal async Task<AdminTopicResponse> CreateTopicAsync(string name)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics",
            new { name }));

        response.EnsureSuccessStatusCode();
        var topic = await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options);
        return topic!;
    }

    protected async Task<SubscriptionDto> CreateSubscriptionAsync(
        Guid topicId,
        string name,
        string eventType,
        int orderIndex = 10,
        string? description = null,
        JsonElement? mapping = null)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topicId}/subscriptions",
            new
            {
                name,
                match_rules = new { event_type = eventType },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = orderIndex,
                description,
                mapping
            }));

        response.EnsureSuccessStatusCode();
        var subscription = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        return subscription!;
    }

    protected async Task SetConnectionStatusAsync(Guid connectionId, string status)
    {
        await using var connection = Fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE connections SET status = @Status WHERE id = @Id",
            new { Status = status, Id = connectionId });
    }

    protected sealed record SubscriptionDto(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        JsonElement? MappingConfig,
        string Status,
        int OrderIndex,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    protected sealed record SubscriptionListDto(
        IReadOnlyList<SubscriptionDto> Items,
        string? NextCursor);
}
