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
    public async Task ListTopics_ExactPageHasNoNextCursor()
    {
        await PostTopicAsync(new { name = "topic-a" });
        await PostTopicAsync(new { name = "topic-b" });

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics?limit=2"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TopicListResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal(2, body.Items.Count);
        Assert.Null(body.NextCursor);
    }

    [Fact]
    public async Task ListTopics_ZeroLimit_UsesDefaultPageSize()
    {
        await PostTopicAsync(new { name = "zero-limit-a" });
        await PostTopicAsync(new { name = "zero-limit-b" });

        var defaultedResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics?limit=0"));
        var explicitResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics?limit=20"));

        Assert.Equal(HttpStatusCode.OK, defaultedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, explicitResponse.StatusCode);

        var defaulted = await defaultedResponse.Content.ReadFromJsonAsync<TopicListResponse>(WebJson);
        var explicitPage = await explicitResponse.Content.ReadFromJsonAsync<TopicListResponse>(WebJson);
        Assert.NotNull(defaulted);
        Assert.NotNull(explicitPage);
        Assert.Equal(2, defaulted.Items.Count);
        Assert.Equal(explicitPage.Items.Select(topic => topic.Id), defaulted.Items.Select(topic => topic.Id));
        Assert.Equal(explicitPage.NextCursor, defaulted.NextCursor);
    }

    [Fact]
    public async Task UpdateTopic_UpdatesDescriptionAndSources_WithoutChangingName()
    {
        var created = await (await PostTopicAsync(new { name = "old-name" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var patch = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}",
            new
            {
                name = "old-name",
                description = "updated",
                sourceConnectionIds = new[] { fixture.SourceConnectionId }
            }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var body = await patch.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("old-name", body.Name);
        Assert.Equal("updated", body.Description);
        Assert.Single(body.SourceConnectionIds);
        Assert.Equal(fixture.SourceConnectionId, body.SourceConnectionIds[0]);
    }

    [Fact]
    public async Task UpdateTopic_InvalidSourceConnection_RollsBackEntireUpdate()
    {
        var created = await (await PostTopicAsync(new
        {
            name = "atomic-update",
            description = "original",
            sourceConnectionIds = new[] { fixture.SourceConnectionId }
        })).Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var patch = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}",
            new
            {
                name = "atomic-update",
                description = "must roll back",
                sourceConnectionIds = new[] { Guid.NewGuid() }
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);

        var get = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        var body = await get.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("original", body.Description);
        Assert.Equal([fixture.SourceConnectionId], body.SourceConnectionIds);
    }

    [Fact]
    public async Task UpdateTopic_ChangingName_Returns422AndPreservesName()
    {
        var created = await (await PostTopicAsync(new { name = "immutable-name" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var patch = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}",
            new { name = "renamed", description = "not applied" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);

        var get = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        var body = await get.Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(body);
        Assert.Equal("immutable-name", body.Name);
        Assert.Null(body.Description);
    }

    [Fact]
    public async Task UpdateTopic_MissingName_Returns422WithRequiredFieldError()
    {
        var created = await (await PostTopicAsync(new { name = "required-name" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var patch = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}",
            new { description = "not applied" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);
        var error = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Topic name is required for update.", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UpdateTopic_UnknownIdWithMissingName_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{Guid.NewGuid()}",
            new { description = "not applied" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTopic_UnknownIdWithInvalidSourceConnection_Returns404()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{Guid.NewGuid()}",
            new
            {
                name = "not-found",
                source_connection_ids = new[] { Guid.NewGuid() }
            }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateTopic_DisabledTopicWithMissingName_PreservesValidationError()
    {
        var created = await (await PostTopicAsync(new { name = "disabled-required-name" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        var deactivate = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}",
            new { description = "not applied" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Topic name is required for update.", error.GetProperty("error").GetString());
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
    public async Task CreateTopic_DestinationOnlySourceConnection_Returns422()
    {
        Guid connectionId = await InsertConnectionAsync("topic_destination_only", "destination");

        var response = await PostTopicAsync(new
        {
            name = "invalid-source-use",
            sourceConnectionIds = new[] { connectionId }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateTopic_MissingSourceVerification_Returns422()
    {
        Guid connectionId = await InsertConnectionAsync(
            "topic_source_verification_required",
            "source",
            requireSourceVerification: true);

        var response = await PostTopicAsync(new
        {
            name = "missing-source-verification",
            sourceConnectionIds = new[] { connectionId }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateTopic_WithDeactivatedSourceConnection_Returns422()
    {
        Guid connectionId = await InsertConnectionAsync(
            "topic_deactivated_source",
            "source",
            status: "disabled");

        var response = await PostTopicAsync(new
        {
            name = "deactivated-source",
            sourceConnectionIds = new[] { connectionId }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateTopic_UnknownCredential_Returns401()
    {
        var response = await client.SendAsync(TenantRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name = "forbidden" },
            AdminApiFixture.InvalidAdminAuthHeader));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTopic_GlobalKey_CanAccessAnyTenant()
    {
        var created = await (await PostTopicAsync(new { name = "global-visible" }))
            .Content.ReadFromJsonAsync<TopicResponse>(WebJson);
        Assert.NotNull(created);

        // Confirm it is accessible with the deployment-wide key (which PostTopicAsync uses).
        var get = await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/topics/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    private Task<HttpResponseMessage> PostTopicAsync(object body) =>
        client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/topics", body));

    private async Task<Guid> InsertConnectionAsync(
        string key,
        string direction,
        bool requireSourceVerification = false,
        string status = "active")
    {
        Guid integrationId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, manifest)
            VALUES (@IntegrationId, @Key, 1, 1, @Key, @Direction, '[]'::jsonb, 'active', @Manifest::jsonb);

            INSERT INTO connections (
                id, tenant_id, integration_id, name, config,
                source_verification, destination_authentication, status)
            VALUES (@ConnectionId, @TenantId, @IntegrationId, @Key, '{}'::jsonb, NULL, NULL, @Status);
            """,
            connection);
        command.Parameters.AddWithValue("IntegrationId", integrationId);
        command.Parameters.AddWithValue("ConnectionId", connectionId);
        command.Parameters.AddWithValue("TenantId", fixture.TenantId);
        command.Parameters.AddWithValue("Key", key);
        command.Parameters.AddWithValue("Direction", direction);
        command.Parameters.AddWithValue("Status", status);
        command.Parameters.AddWithValue("Manifest", TestIntegrationManifest.Create(
            key,
            key,
            direction,
            sourceVerificationSchemes: requireSourceVerification ? ["github_hmac_sha256"] : []));
        await command.ExecuteNonQueryAsync();
        return connectionId;
    }

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
