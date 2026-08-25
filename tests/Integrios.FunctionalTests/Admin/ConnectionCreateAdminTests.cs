using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Authoring.Connections;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class ConnectionCreateAdminTests : ConnectionAdminTestBase
{
    public ConnectionCreateAdminTests(AdminApiFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task CreateConnection_ReturnsCreated_WithCorrectBody()
    {
        var response = await PostConnectionAsync("erp-sink", "http://localhost:5054/sink/erp");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
        var body = await response.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body!.TenantId.ShouldBe(Fixture.TenantId);
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
            $"/admin/tenants/{Fixture.TenantId}/connections",
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
            $"/admin/tenants/{Fixture.TenantId}/connections",
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
            $"/admin/tenants/{Fixture.TenantId}/connections",
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
            $"/admin/tenants/{Fixture.TenantId}/connections",
            new
            {
                connector_id = connectorId,
                name = "erp-auth",
                config = new { base_uri = "http://localhost:5054/sink/erp-auth" }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
