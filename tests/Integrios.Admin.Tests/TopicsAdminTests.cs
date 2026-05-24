using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Topics;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Integrios.Admin.Tests;

public sealed class TopicsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    // Matches ASP.NET Core's camelCase defaults for positional record deserialization.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public TopicsAdminTests(AdminApiFixture fixture)
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
    public async Task CreateTopic_ReturnsCreated_WithCorrectBody()
    {
        var response = await PostTopicAsync(new
        {
            name = "payments",
            description = "Payment events stream"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(fixture.TenantId, body.TenantId);
        Assert.Equal("payments", body.Name);
        Assert.Equal("Payment events stream", body.Description);
        Assert.Equal("active", body.Status);
        Assert.NotEqual(default, body.Id);
        Assert.Empty(body.SourceConnectionIds);
    }

    [Fact]
    public async Task CreateTopic_WithSourceConnections_RoundTrips()
    {
        var response = await PostTopicAsync(new
        {
            name = "orders",
            sourceConnectionIds = new[] { fixture.SourceConnectionId }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Single(body.SourceConnectionIds);
        Assert.Equal(fixture.SourceConnectionId, body.SourceConnectionIds[0]);
    }

    [Fact]
    public async Task GetTopicById_ReturnsTopicWithSources()
    {
        var created = await (await PostTopicAsync(new
        {
            name = "inventory",
            sourceConnectionIds = new[] { fixture.SourceConnectionId }
        })).Content.ReadFromJsonAsync<TopicResponse>(WebJson);

        Assert.NotNull(created);

        var get = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Single(body.SourceConnectionIds);
        Assert.Equal(fixture.SourceConnectionId, body.SourceConnectionIds[0]);
    }

    [Fact]
    public async Task GetTopicById_UnknownId_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListTopics_ReturnsCursorPaginatedResults()
    {
        await PostTopicAsync(new { name = "topic-a" });
        await PostTopicAsync(new { name = "topic-b" });
        await PostTopicAsync(new { name = "topic-c" });

        var page1 = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics?limit=2"));
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);

        var body1 = await page1.Content.ReadFromJsonAsync<TopicListResponse>(WebJson);
        Assert.NotNull(body1);
        Assert.Equal(2, body1.Items.Count);
        Assert.NotNull(body1.NextCursor);

        var page2 = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);

        var body2 = await page2.Content.ReadFromJsonAsync<TopicListResponse>(WebJson);
        Assert.NotNull(body2);
        Assert.Single(body2.Items);
        Assert.Null(body2.NextCursor);

        var allNames = body1.Items.Select(t => t.Name).Concat(body2.Items.Select(t => t.Name)).ToList();
        Assert.Contains("topic-a", allNames);
        Assert.Contains("topic-b", allNames);
        Assert.Contains("topic-c", allNames);
    }

    [Fact]
    public async Task UpdateTopic_UpdatesNameDescriptionAndSources()
    {
        var created = await (await PostTopicAsync(new { name = "old-name" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var patch = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}",
            new
            {
                name = "new-name",
                description = "updated",
                sourceConnectionIds = new[] { fixture.SourceConnectionId }
            }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var body = await patch.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("new-name", body.Name);
        Assert.Equal("updated", body.Description);
        Assert.Single(body.SourceConnectionIds);
        Assert.Equal(fixture.SourceConnectionId, body.SourceConnectionIds[0]);
    }

    [Fact]
    public async Task DeactivateTopic_Returns200_AndStatusBecomesInactive()
    {
        var created = await (await PostTopicAsync(new { name = "to-deactivate" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var deactivate = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var get = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("disabled", body.Status);
    }

    [Fact]
    public async Task DeactivateTopic_UnknownId_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{Guid.NewGuid()}/deactivate"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateTopic_DuplicateName_ReturnsConflict()
    {
        await PostTopicAsync(new { name = "payments" });
        var response = await PostTopicAsync(new { name = "payments" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateTopic_WrongTenantKey_Returns403()
    {
        var response = await client.SendAsync(TenantRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name = "forbidden" },
            fixture.OtherTenantAdminKey));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetTopic_GlobalKey_CanAccessAnyTenant()
    {
        var created = await (await PostTopicAsync(new { name = "global-visible" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        // confirm it's accessible with the global key (which PostTopicAsync uses)
        var get = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    private Task<HttpResponseMessage> PostTopicAsync(object body) =>
        client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/topics", body));

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }

    private HttpRequestMessage TenantRequest(HttpMethod method, string url, object? body, string authHeader)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", authHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }
}
