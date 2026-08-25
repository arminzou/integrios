using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Authoring.Connections;
using Integrios.Admin.Endpoints;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class ConnectionUpdateAdminTests : ConnectionAdminTestBase
{
    public ConnectionUpdateAdminTests(AdminApiFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task UpdateConnection_WithSupportedAuth_PersistsAndReturnsAuth()
    {
        Guid connectorId = await InsertConnectorAsync("update_auth_sink", ["api_key_header"]);
        Guid connectionId = await InsertConnectionAsync(connectorId, "erp-auth");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{Fixture.TenantId}/connections/{connectionId}",
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
            $"/admin/tenants/{Fixture.TenantId}/connections/{connectionId}",
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
            $"/admin/tenants/{Fixture.TenantId}/connections/{connectionId}",
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
            $"/admin/tenants/{Fixture.TenantId}/connections/{connectionId}",
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
            $"/admin/tenants/{Fixture.TenantId}/connections/{connectionId}",
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
            $"/admin/tenants/{Fixture.TenantId}/topics",
            new { name = "in-use-authentication-topic" }));
        AdminTopicResponse topic = (await topicResponse.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;
        HttpResponseMessage subscriptionResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
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
            $"/admin/tenants/{Fixture.TenantId}/connections/{created.Id}/deactivate"));
        deactivateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage rotateResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{Fixture.TenantId}/connections/{created.Id}",
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
            $"/admin/tenants/{Fixture.TenantId}/connections/{created.Id}",
            new
            {
                name = created.Name,
                config = new { base_uri = "http://localhost:5054/sink/in-use-authentication" }
            }));

        updateResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
