using System.Net;
using System.Net.Http.Json;
using Integrios.Admin.Endpoints;
using Integrios.Application.Authoring.Destinations;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class DestinationsAdminTests(AdminApiFixture fixture) : SubscriptionAdminTestBase(fixture)
{
    // Fanout joins destinations without filtering on status, which is only safe because a
    // Destination an active Subscription still points at cannot reach that status in the first
    // place. This is the test for that premise.
    [Fact]
    public async Task Deactivate_IsRefusedWhileAnActiveSubscriptionReferencesTheDestination()
    {
        AdminTopicResponse topic = await CreateTopicAsync("deactivation-topic");
        SubscriptionDto subscription = await CreateSubscriptionAsync(topic.Id, "holds-the-destination", "order.placed");

        HttpResponseMessage refused = await client.SendAsync(AdminRequest(
            HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/destinations/{Fixture.DestinationId}/deactivate"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await StatusOfAsync(Fixture.DestinationId)).ShouldBe("active");

        // Releasing the reference has to let it through, or the assertion above would hold for a
        // Destination that simply never deactivates.
        (await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{subscription.Id}/deactivate")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(AdminRequest(
            HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/destinations/{Fixture.DestinationId}/deactivate")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusOfAsync(Fixture.DestinationId)).ShouldBe("disabled");
    }

    // A Destination name is how an Operator refers to one in configuration and runbooks, so two
    // Destinations answering to the same name is ambiguity the database is expected to refuse and
    // the API is expected to report as a conflict rather than a fault.
    [Fact]
    public async Task DuplicateName_IsRefusedWithConflictOnCreateAndOnRename()
    {
        (await CreateDestinationAsync("seeded-destination")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        HttpResponseMessage created = await CreateDestinationAsync("renameable-destination");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        DestinationDto destination = (await created.Content.ReadFromJsonAsync<DestinationDto>(HostJson.Options))!;

        HttpResponseMessage renamed = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{Fixture.TenantId}/destinations/{destination.Id}",
            new { name = "seeded-destination", configuration = new { base_uri = "http://localhost:5054/sink" } }));

        renamed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private Task<HttpResponseMessage> CreateDestinationAsync(string name) => client.SendAsync(AdminRequest(
        HttpMethod.Post,
        $"/admin/tenants/{Fixture.TenantId}/destinations",
        new
        {
            connector_id = Fixture.HttpConnectorId,
            name,
            configuration = new { base_uri = "http://localhost:5054/sink" },
        }));

    private async Task<string?> StatusOfAsync(Guid destinationId)
    {
        await using var connection = Fixture.CreateConnection();
        await connection.OpenAsync();
        return await Dapper.SqlMapper.QuerySingleOrDefaultAsync<string>(
            connection, "SELECT status FROM destinations WHERE id = @Id", new { Id = destinationId });
    }
}
