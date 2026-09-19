using System.Net;
using System.Net.Http.Json;
using Integrios.Admin.Endpoints;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Authoring.Sources;
using Integrios.Application.Authoring.Tenants;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

// A Tenant deactivation is reversible and never cascades: it fences intake while leaving the
// Tenant's Sources, Destinations, and Subscriptions in the status the Operator set.
public sealed class TenantLifecycleAdminTests(AdminApiFixture fixture) : SubscriptionAdminTestBase(fixture)
{
    [Fact]
    public async Task DeactivateAndActivate_ToggleTenantWithoutCascadingAndAreIdempotent()
    {
        AdminTopicResponse topic = await CreateTopicAsync("lifecycle");
        HttpResponseMessage createSource = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/sources",
            new
            {
                connector_id = Fixture.HttpConnectorId,
                topic_id = topic.Id,
                name = "lifecycle-intake",
                type = "event_api",
                configuration = new { },
                event_types = new[] { "probe.created" },
            }));
        createSource.StatusCode.ShouldBe(HttpStatusCode.Created);
        SourceDto source = (await createSource.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        (await client.SendAsync(AdminRequest(
                HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/sources/{source.Id}/activate")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(AdminRequest(
                HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/deactivate")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TenantStatusAsync()).ShouldBe("inactive");
        (await SourceStatusAsync(source.Id)).ShouldBe("active");
        (await DestinationStatusAsync()).ShouldBe("active");

        // An existing Tenant already in the requested state is not a missing resource.
        (await client.SendAsync(AdminRequest(
                HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/deactivate")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(AdminRequest(
                HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/activate")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TenantStatusAsync()).ShouldBe("active");
        (await client.SendAsync(AdminRequest(
                HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/activate")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeactivateAndActivate_UnknownTenant_Returns404()
    {
        (await client.SendAsync(AdminRequest(
                HttpMethod.Post, $"/admin/tenants/{Guid.NewGuid()}/deactivate")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(AdminRequest(
                HttpMethod.Post, $"/admin/tenants/{Guid.NewGuid()}/activate")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<string> TenantStatusAsync()
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}"));
        TenantDto tenant = (await response.Content.ReadFromJsonAsync<TenantDto>(HostJson.Options))!;
        return tenant.Status;
    }

    private async Task<string> SourceStatusAsync(Guid sourceId)
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}/sources/{sourceId}"));
        SourceDto source = (await response.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        return source.Status;
    }

    private async Task<string> DestinationStatusAsync()
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}/destinations/{Fixture.DestinationId}"));
        DestinationDto destination = (await response.Content.ReadFromJsonAsync<DestinationDto>(HostJson.Options))!;
        return destination.Status;
    }
}
