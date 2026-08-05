using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Connections;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Integrios.Tests.Shared;

namespace Integrios.Application.FunctionalTests.Admin;

public sealed class ConnectionsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private static readonly Guid HttpIntegrationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private HttpClient client = default!;

    public ConnectionsAdminTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateConnection_ReturnsCreated_WithCorrectBody()
    {
        var response = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var body = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal(fixture.TenantId, body!.TenantId);
        Assert.Equal("erp-sink", body.Name);
        Assert.Equal("active", body.Status);
        Assert.Null(body.SourceVerification);
        Assert.Null(body.DestinationAuthentication);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5054/sink/private")]
    [InlineData("http://10.20.30.40/sink/private")]
    [InlineData("https://[::1]/sink/private")]
    public async Task CreateConnection_PrivateOrLoopbackHttpDestination_ReturnsCreated(string url)
    {
        var response = await PostConnectionAsync($"private-{Guid.NewGuid():N}", url);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://example.test/sink")]
    [InlineData("file:///tmp/sink")]
    [InlineData("not a url")]
    public async Task CreateConnection_UnusedInvalidHttpDestination_IsStoredUntilDestinationUse(string url)
    {
        var response = await PostConnectionAsync($"invalid-{Guid.NewGuid():N}", url);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_UnusedMissingDestinationUrl_IsStoredUntilDestinationUse()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integration_id = HttpIntegrationId,
                name = "missing-url",
                config = new { }
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_SourceOnlyIntegration_AllowsFreeFormConfig()
    {
        Guid integrationId = await InsertIntegrationAsync(
            $"source_only_{Guid.NewGuid():N}",
            [],
            "source");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integration_id = integrationId,
                name = "source-without-url",
                config = new { source_name = "orders" }
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_WithSupportedAuth_PersistsAndHidesSecretRefs()
    {
        Guid integrationId = await InsertIntegrationAsync("erp_sink_auth", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "erp-auth",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { api_key = "erp_api_key" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.NotNull(body!.DestinationAuthentication);
        Assert.Equal("api_key_header", body.DestinationAuthentication!.Scheme);
        Assert.Equal("X-Api-Key", body.DestinationAuthentication.Config.GetProperty("header_name").GetString());
    }

    [Fact]
    public async Task CreateConnection_DuplicateName_ReturnsConflict()
    {
        var first = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp-2");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_SameName_DifferentTenant_ReturnsCreated()
    {
        var first = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Guid otherTenantId = await GetTenantIdBySlugAsync("other-tenant");

        var second = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{otherTenantId}/connections",
            new
            {
                integration_id = HttpIntegrationId,
                name = "erp-sink",
                config = new { base_uri = "http://localhost:5054/sink/other" }
            }));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_MissingIntegration_Returns422()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integration_id = Guid.NewGuid(),
                name = "bad-connection",
                config = new { base_uri = "http://localhost:5054/sink/x" }
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_UnsupportedScheme_Returns422()
    {
        Guid integrationId = await InsertIntegrationAsync("unsupported_auth_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "erp-auth",
            "bearer_token",
            new { },
            new { token = "erp_token" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_UnimplementedScheme_Returns422()
    {
        Guid integrationId = await InsertIntegrationAsync("oauth_sink", ["oauth_client_credentials"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "erp-auth",
            "oauth_client_credentials",
            new { token_url = "https://auth.example/token" },
            new { client_secret = "oauth_secret" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_MissingRequiredConfig_Returns422()
    {
        Guid integrationId = await InsertIntegrationAsync("missing_config_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "erp-auth",
            "api_key_header",
            new { },
            new { api_key = "erp_api_key" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_MissingRequiredSecretRef_Returns422()
    {
        Guid integrationId = await InsertIntegrationAsync("missing_secret_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "erp-auth",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_InvalidSecretReference_Returns422()
    {
        Guid integrationId = await InsertIntegrationAsync("invalid_secret_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "erp-auth",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { api_key = "Bad-Ref" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("_leading")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")]
    public async Task CreateConnection_RejectsSecretReferenceOutsideFlatNameContract(string reference)
    {
        Guid integrationId = await InsertIntegrationAsync($"invalid_ref_{Guid.NewGuid():N}", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "invalid-reference",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { api_key = reference });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("Integrios-Delivery-Id")]
    [InlineData("integrios-attempt-number")]
    public async Task CreateConnection_ReservedDeliveryHeader_Returns422(string headerName)
    {
        Guid integrationId = await InsertIntegrationAsync($"reserved_{Guid.NewGuid():N}", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            integrationId,
            "reserved-header",
            "api_key_header",
            new { header_name = headerName },
            new { api_key = "erp_api_key" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateConnection_UnusedConnectionMayOmitDestinationAuthentication()
    {
        Guid integrationId = await InsertIntegrationAsync("closed_auth_sink", ["api_key_header"]);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integration_id = integrationId,
                name = "erp-auth",
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" }
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UpdateConnection_WithSupportedAuth_PersistsAndReturnsAuth()
    {
        Guid integrationId = await InsertIntegrationAsync("update_auth_sink", ["api_key_header"]);
        Guid connectionId = await InsertConnectionAsync(integrationId, "erp-auth");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "erp-auth",
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" },
                destination_authentication = new
                {
                    scheme = "api_key_header",
                    config = new { header_name = "X-Api-Key" },
                    secret_refs = new { api_key = "erp_api_key" }
                }
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.NotNull(body!.DestinationAuthentication);
        Assert.Equal("api_key_header", body.DestinationAuthentication!.Scheme);
    }

    [Fact]
    public async Task UpdateConnection_UnusedInvalidHttpDestination_IsStoredUntilDestinationUse()
    {
        Guid connectionId = await InsertConnectionAsync(HttpIntegrationId, "invalid-update");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "invalid-update",
                config = new { base_uri = "ftp://example.test/sink" }
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateConnection_UnusedDestinationCapableIntegrationMayOmitConfig()
    {
        Guid connectionId = await InsertConnectionAsync(HttpIntegrationId, "omitted-config");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "omitted-config"
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateConnection_SourceOnlyIntegration_AllowsFreeFormConfig()
    {
        Guid integrationId = await InsertIntegrationAsync(
            $"source_only_update_{Guid.NewGuid():N}",
            [],
            "source");
        Guid connectionId = await InsertConnectionAsync(integrationId, "source-update");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "source-update",
                config = new { source_name = "updated-orders" }
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateConnection_ReservedDeliveryHeader_Returns422()
    {
        Guid integrationId = await InsertIntegrationAsync("update_reserved_header", ["api_key_header"]);
        Guid connectionId = await InsertConnectionAsync(integrationId, "erp-auth");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "erp-auth",
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" },
                destination_authentication = new
                {
                    scheme = "api_key_header",
                    config = new { header_name = "INTEGRIOS-ATTEMPT-ID" },
                    secret_refs = new { api_key = "erp_api_key" }
                }
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task DeactivatedConnection_CanRotateButCannotRemoveAuthenticationRequiredByActiveSubscription()
    {
        Guid integrationId = await InsertIntegrationAsync("in_use_authentication_sink", ["api_key_header"]);
        HttpResponseMessage createdResponse = await PostConnectionWithAuthAsync(
            integrationId,
            "in-use-authentication",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { api_key = "erp_api_key" });
        ConnectionDto created = (await createdResponse.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options))!;

        HttpResponseMessage topicResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name = "in-use-authentication-topic" }));
        AdminTopicResponse topic = (await topicResponse.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;
        HttpResponseMessage subscriptionResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "in-use-authentication-subscription",
                match_rules = new { event_type = "test.event" },
                destination_connection_id = created.Id,
                order_index = 0
            }));
        Assert.Equal(HttpStatusCode.Created, subscriptionResponse.StatusCode);

        HttpResponseMessage deactivateResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections/{created.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);

        HttpResponseMessage rotateResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{created.Id}",
            new
            {
                name = created.Name,
                config = new { base_uri = "http://localhost:5054/sink/in-use-authentication" },
                destination_authentication = new
                {
                    scheme = "api_key_header",
                    config = new { header_name = "X-Rotated-Key" },
                    secret_refs = new { api_key = "rotated_erp_api_key" }
                }
            }));
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);

        HttpResponseMessage updateResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{created.Id}",
            new
            {
                name = created.Name,
                config = new { base_uri = "http://localhost:5054/sink/in-use-authentication" }
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, updateResponse.StatusCode);
    }

    private Task<HttpResponseMessage> PostConnectionAsync(string name, string url) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integration_id = HttpIntegrationId,
                name,
                config = new { base_uri = url }
            }));

    private Task<HttpResponseMessage> PostConnectionWithAuthAsync(
        Guid integrationId,
        string name,
        string scheme,
        object authConfig,
        object secretRefs) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                integration_id = integrationId,
                name,
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" },
                destination_authentication = new
                {
                    scheme,
                    config = authConfig,
                    secret_refs = secretRefs
                }
            }));

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
        {
            msg.Content = JsonContent.Create(body);
        }

        return msg;
    }

    private async Task<Guid> InsertIntegrationAsync(
        string key,
        string[] supportedAuthSchemes,
        string direction = "destination")
    {
        Guid id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, description, manifest, created_at, updated_at)
            VALUES (
                @Id, @Key, 1, 1, @Name, @Direction,
                @SupportedAuthSchemes::jsonb, 'active', 'test integration', @Manifest::jsonb, now(), now());
            """,
            connection);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("Key", key);
        cmd.Parameters.AddWithValue("Name", key);
        cmd.Parameters.AddWithValue("Direction", direction);
        cmd.Parameters.AddWithValue("SupportedAuthSchemes", JsonSerializer.Serialize(supportedAuthSchemes));
        cmd.Parameters.AddWithValue("Manifest", TestIntegrationManifest.Create(
            key, key, direction, supportedAuthSchemes));
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<Guid> InsertConnectionAsync(Guid integrationId, string name)
    {
        Guid id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO connections (id, tenant_id, integration_id, name, config, source_verification, destination_authentication, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @IntegrationId, @Name, '{"base_uri":"http://localhost:5054/sink/erp-auth"}'::jsonb, NULL, NULL, 'active', NULL, NULL, now(), now());
            """,
            connection);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("TenantId", fixture.TenantId);
        cmd.Parameters.AddWithValue("IntegrationId", integrationId);
        cmd.Parameters.AddWithValue("Name", name);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<Guid> GetTenantIdBySlugAsync(string slug)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT id FROM tenants WHERE slug = @Slug LIMIT 1", connection);
        cmd.Parameters.AddWithValue("Slug", slug);
        object? result = await cmd.ExecuteScalarAsync();
        return (Guid)result!;
    }
}
