using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Topics;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Admin.Tests;

public sealed class SubscriptionsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public SubscriptionsAdminTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateSubscription_ReturnsCreated_WithCorrectBody()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                matchRules = new { event_type = "payment.created" },
                destinationConnectionId = fixture.SourceConnectionId,
                dlqEnabled = true,
                orderIndex = 10,
                description = "Primary ERP delivery"
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(topic.Id, body.TopicId);
        Assert.Equal(fixture.TenantId, body.TenantId);
        Assert.Equal("erp-sink", body.Name);
        Assert.Equal(fixture.SourceConnectionId, body.DestinationConnectionId);
        Assert.True(body.DlqEnabled);
        Assert.Equal(10, body.OrderIndex);
        Assert.Equal("active", body.Status);
        Assert.Equal("payment.created", body.MatchRules.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task GetSubscriptionById_ReturnsSubscription()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Equal("erp-sink", body.Name);
    }

    [Fact]
    public async Task ListSubscriptions_ReturnsCursorPaginatedResults()
    {
        var topic = await CreateTopicAsync("payments");
        await CreateSubscriptionAsync(topic.Id, "sub-a", "payment.created", orderIndex: 1);
        await CreateSubscriptionAsync(topic.Id, "sub-b", "payment.updated", orderIndex: 2);
        await CreateSubscriptionAsync(topic.Id, "sub-c", "payment.failed", orderIndex: 3);

        var page1 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2"));
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);

        var body1 = await page1.Content.ReadFromJsonAsync<SubscriptionListResponse>(WebJson);
        Assert.NotNull(body1);
        Assert.Equal(2, body1.Items.Count);
        Assert.NotNull(body1.NextCursor);

        var page2 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);

        var body2 = await page2.Content.ReadFromJsonAsync<SubscriptionListResponse>(WebJson);
        Assert.NotNull(body2);
        Assert.Single(body2.Items);
        Assert.Null(body2.NextCursor);
    }

    [Fact]
    public async Task UpdateSubscription_UpdatesEditableFields()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                matchRules = new { event_type = "payment.updated" },
                destinationConnectionId = fixture.SourceConnectionId,
                dlqEnabled = false,
                orderIndex = 25,
                description = "Updated ERP delivery"
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("erp-sink-v2", body.Name);
        Assert.False(body.DlqEnabled);
        Assert.Equal(25, body.OrderIndex);
        Assert.Equal("payment.updated", body.MatchRules.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task DeactivateSubscription_Returns200_AndStatusBecomesDisabled()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var deactivate = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var get = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<SubscriptionResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("disabled", body.Status);
    }

    private async Task<TopicResponse> CreateTopicAsync(string name)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name }));

        response.EnsureSuccessStatusCode();
        var topic = await response.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        return topic!;
    }

    private async Task<SubscriptionResponse> CreateSubscriptionAsync(
        Guid topicId,
        string name,
        string eventType,
        bool dlqEnabled = true,
        int orderIndex = 10,
        string? description = null)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions",
            new
            {
                name,
                matchRules = new { event_type = eventType },
                destinationConnectionId = fixture.SourceConnectionId,
                dlqEnabled,
                orderIndex,
                description
            }));

        response.EnsureSuccessStatusCode();
        var subscription = await response.Content.ReadFromJsonAsync<SubscriptionResponse>(WebJson);
        return subscription!;
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }

    private sealed record SubscriptionResponse(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        bool DlqEnabled,
        string Status,
        int OrderIndex,
        string? Description);

    private sealed record SubscriptionListResponse(
        IReadOnlyList<SubscriptionResponse> Items,
        string? NextCursor);
}
