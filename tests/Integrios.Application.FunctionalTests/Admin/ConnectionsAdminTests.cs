using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Application.Authoring.Connections;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Application.FunctionalTests.Admin;

public sealed class ConnectionsAdminTests : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private static readonly Guid HttpConnectorId = Guid.Parse("00000000-0000-0000-0000-000000000001");
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

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
        var body = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body!.TenantId.ShouldBe(fixture.TenantId);
        body.Name.ShouldBe("erp-sink");
        body.Status.ShouldBe("active");
        body.SourceVerification.ShouldBeNull();
        body.DestinationAuthentication.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://127.0.0.1:5054/sink/private")]
    [InlineData("http://10.20.30.40/sink/private")]
    [InlineData("https://[::1]/sink/private")]
    public async Task CreateConnection_PrivateOrLoopbackHttpDestination_ReturnsCreated(string url)
    {
        var response = await PostConnectionAsync($"private-{Guid.NewGuid():N}", url);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://example.test/sink")]
    [InlineData("file:///tmp/sink")]
    [InlineData("not a url")]
    public async Task CreateConnection_UnusedInvalidHttpDestination_IsStoredUntilDestinationUse(string url)
    {
        var response = await PostConnectionAsync($"invalid-{Guid.NewGuid():N}", url);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateConnection_UnusedMissingDestinationUrl_IsStoredUntilDestinationUse()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                connector_id = HttpConnectorId,
                name = "missing-url",
                config = new { }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateConnection_SourceOnlyConnector_AllowsFreeFormConfig()
    {
        Guid connectorId = await InsertConnectorAsync(
            $"source_only_{Guid.NewGuid():N}",
            [],
            "source");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                connector_id = connectorId,
                name = "source-without-url",
                config = new { source_name = "orders" }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateConnection_WithSupportedAuth_PersistsAndHidesSecretRefs()
    {
        Guid connectorId = await InsertConnectorAsync("erp_sink_auth", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "erp-auth",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { api_key = "erp_api_key" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body!.DestinationAuthentication.ShouldNotBeNull();
        body.DestinationAuthentication!.Scheme.ShouldBe("api_key_header");
        body.DestinationAuthentication.Config.GetProperty("header_name").GetString().ShouldBe("X-Api-Key");
    }

    [Fact]
    public async Task CreateConnection_DuplicateName_ReturnsConflict()
    {
        var first = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp-2");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateConnection_SameName_DifferentTenant_ReturnsCreated()
    {
        var first = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid otherTenantId = await GetTenantIdBySlugAsync("other-tenant");

        var second = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{otherTenantId}/connections",
            new
            {
                connector_id = HttpConnectorId,
                name = "erp-sink",
                config = new { base_uri = "http://localhost:5054/sink/other" }
            }));

        second.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateConnection_MissingConnector_Returns422()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                connector_id = Guid.NewGuid(),
                name = "bad-connection",
                config = new { base_uri = "http://localhost:5054/sink/x" }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateConnection_UnsupportedScheme_Returns422()
    {
        Guid connectorId = await InsertConnectorAsync("unsupported_auth_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "erp-auth",
            "bearer_token",
            new { },
            new { token = "erp_token" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateConnection_UnimplementedScheme_Returns422()
    {
        Guid connectorId = await InsertConnectorAsync("oauth_sink", ["oauth_client_credentials"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "erp-auth",
            "oauth_client_credentials",
            new { token_url = "https://auth.example/token" },
            new { client_secret = "oauth_secret" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateConnection_MissingRequiredConfig_Returns422()
    {
        Guid connectorId = await InsertConnectorAsync("missing_config_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "erp-auth",
            "api_key_header",
            new { },
            new { api_key = "erp_api_key" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateConnection_MissingRequiredSecretRef_Returns422()
    {
        Guid connectorId = await InsertConnectorAsync("missing_secret_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "erp-auth",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateConnection_InvalidSecretReference_Returns422()
    {
        Guid connectorId = await InsertConnectorAsync("invalid_secret_sink", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "erp-auth",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { api_key = "Bad-Ref" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("_leading")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")]
    public async Task CreateConnection_RejectsSecretReferenceOutsideFlatNameContract(string reference)
    {
        Guid connectorId = await InsertConnectorAsync($"invalid_ref_{Guid.NewGuid():N}", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "invalid-reference",
            "api_key_header",
            new { header_name = "X-Api-Key" },
            new { api_key = reference });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("Integrios-Delivery-Id")]
    [InlineData("integrios-attempt-number")]
    public async Task CreateConnection_ReservedDeliveryHeader_Returns422(string headerName)
    {
        Guid connectorId = await InsertConnectorAsync($"reserved_{Guid.NewGuid():N}", ["api_key_header"]);

        var response = await PostConnectionWithAuthAsync(
            connectorId,
            "reserved-header",
            "api_key_header",
            new { header_name = headerName },
            new { api_key = "erp_api_key" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateConnection_UnusedConnectionMayOmitDestinationAuthentication()
    {
        Guid connectorId = await InsertConnectorAsync("closed_auth_sink", ["api_key_header"]);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                connector_id = connectorId,
                name = "erp-auth",
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UpdateConnection_WithSupportedAuth_PersistsAndReturnsAuth()
    {
        Guid connectorId = await InsertConnectorAsync("update_auth_sink", ["api_key_header"]);
        Guid connectionId = await InsertConnectionAsync(connectorId, "erp-auth");

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

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body!.DestinationAuthentication.ShouldNotBeNull();
        body.DestinationAuthentication!.Scheme.ShouldBe("api_key_header");
    }

    [Fact]
    public async Task UpdateConnection_UnusedInvalidHttpDestination_IsStoredUntilDestinationUse()
    {
        Guid connectionId = await InsertConnectionAsync(HttpConnectorId, "invalid-update");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "invalid-update",
                config = new { base_uri = "ftp://example.test/sink" }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateConnection_UnusedDestinationCapableConnectorMayOmitConfig()
    {
        Guid connectionId = await InsertConnectionAsync(HttpConnectorId, "omitted-config");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "omitted-config"
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateConnection_SourceOnlyConnector_AllowsFreeFormConfig()
    {
        Guid connectorId = await InsertConnectorAsync(
            $"source_only_update_{Guid.NewGuid():N}",
            [],
            "source");
        Guid connectionId = await InsertConnectionAsync(connectorId, "source-update");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{connectionId}",
            new
            {
                name = "source-update",
                config = new { source_name = "updated-orders" }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateConnection_ReservedDeliveryHeader_Returns422()
    {
        Guid connectorId = await InsertConnectorAsync("update_reserved_header", ["api_key_header"]);
        Guid connectionId = await InsertConnectionAsync(connectorId, "erp-auth");

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

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task DeactivatedConnection_CanRotateButCannotRemoveAuthenticationRequiredByActiveSubscription()
    {
        Guid connectorId = await InsertConnectorAsync("in_use_authentication_sink", ["api_key_header"]);
        HttpResponseMessage createdResponse = await PostConnectionWithAuthAsync(
            connectorId,
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
        subscriptionResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage deactivateResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections/{created.Id}/deactivate"));
        deactivateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

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
        rotateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage updateResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/connections/{created.Id}",
            new
            {
                name = created.Name,
                config = new { base_uri = "http://localhost:5054/sink/in-use-authentication" }
            }));

        updateResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private Task<HttpResponseMessage> PostConnectionAsync(string name, string url) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                connector_id = HttpConnectorId,
                name,
                config = new { base_uri = url }
            }));

    private Task<HttpResponseMessage> PostConnectionWithAuthAsync(
        Guid connectorId,
        string name,
        string scheme,
        object authConfig,
        object secretRefs) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new
            {
                connector_id = connectorId,
                name,
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" },
                destination_authentication = new
                {
                    scheme,
                    config = authConfig,
                    secret_refs = secretRefs
                }
            }));

    private async Task<Guid> InsertConnectorAsync(
        string key,
        string[] supportedAuthSchemes,
        string direction = "destination")
    {
        Guid id = Guid.NewGuid();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO connectors (
                id, {{{fixture.KeyColumn}}}, contract_version, manifest_schema_version, name, direction,
                status, description, manifest, created_at, updated_at)
            VALUES (
                @Id, @Key, 1, 1, @Name, @Direction,
                'active', 'test connector', {{{fixture.Json("@Manifest")}}}, {{{fixture.Now}}}, {{{fixture.Now}}});
            """, new
            {
                Id = id,
                Key = key,
                Name = key,
                Direction = direction,
                Manifest = TestConnectorManifest.Create(key, key, direction, supportedAuthSchemes)
            });

        return id;
    }

    private async Task<Guid> InsertConnectionAsync(Guid connectorId, string name)
    {
        Guid id = Guid.NewGuid();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync($$$"""
            INSERT INTO connections (id, tenant_id, connector_id, name, config, source_verification, destination_authentication, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @ConnectorId, @Name, {{{fixture.Json("@Config")}}}, NULL, NULL, 'active', NULL, NULL, {{{fixture.Now}}}, {{{fixture.Now}}});
            """, new { Id = id, fixture.TenantId, ConnectorId = connectorId, Name = name, Config = "{\"base_uri\":\"http://localhost:5054/sink/erp-auth\"}" });

        return id;
    }

    private async Task<Guid> GetTenantIdBySlugAsync(string slug)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<Guid>("SELECT id FROM tenants WHERE slug = @Slug", new { Slug = slug });
    }
}
