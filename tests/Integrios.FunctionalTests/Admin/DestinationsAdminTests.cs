using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    // ADR-0088 puts uniqueness on keys, and a Destination has none: its name is a label an Operator
    // can correct, while the identifier is what anything else refers to. Two Destinations may
    // therefore answer to one name, and a rename onto an existing name is an ordinary update.
    [Fact]
    public async Task DuplicateName_IsAcceptedOnCreateAndOnRename()
    {
        (await CreateDestinationAsync("seeded-destination")).StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage created = await CreateDestinationAsync("renameable-destination");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        DestinationDto destination = (await created.Content.ReadFromJsonAsync<DestinationDto>(HostJson.Options))!;

        HttpResponseMessage renamed = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{Fixture.TenantId}/destinations/{destination.Id}",
            new
            {
                name = "seeded-destination",
                configuration = new { base_uri = "http://localhost:5054/sink" },
                authentication = (object?)null,
                environment = (string?)null,
                description = (string?)null,
            }));

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListNamesAsync()).Count(name => name == "seeded-destination").ShouldBeGreaterThanOrEqualTo(2);
    }

    private async Task<IReadOnlyList<string>> ListNamesAsync()
    {
        JsonElement body = await (await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}/destinations?limit=100")))
            .Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];
    }

    // An update replaces the whole resource, so an omitted field is a malformed body rather than an
    // instruction to keep what is stored. Answering anything but a refusal here means the field was
    // written as null and whatever it held is gone.
    [Theory]
    [InlineData("name")]
    [InlineData("configuration")]
    [InlineData("authentication")]
    [InlineData("environment")]
    [InlineData("description")]
    public async Task Update_OmittingAnyField_IsRefusedRatherThanWritingADefault(string omitted)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "seeded-destination",
            ["configuration"] = new { base_uri = "http://localhost:5054/sink" },
            ["authentication"] = null,
            ["environment"] = "production",
            ["description"] = "kept",
        };
        body.Remove(omitted);

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Put, $"/admin/tenants/{Fixture.TenantId}/destinations/{Fixture.DestinationId}", body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // The reference names are the only part of a credential a client is given back, so an edit that
    // changes nothing has to be expressible. Before they were returned, this was impossible: the
    // form could only send an empty object and the Destination became uneditable.
    [Fact]
    public async Task AnAuthenticatedDestination_CanBeResubmittedUnchanged()
    {
        Guid connectorId = await Fixture.ApplyConnectorManifestAsync(
            "round_trip",
            TestConnectorManifest.Create("round_trip", "Round trip", "destination", authenticationSchemes: ["bearer_token"]));

        HttpResponseMessage created = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/destinations",
            new
            {
                connector_id = connectorId,
                name = "round-trip-destination",
                configuration = new { base_uri = "http://localhost:5054/sink" },
                authentication = new { scheme = "bearer_token", config = new { }, secret_refs = new { token = "round_trip_token" } },
            }));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        JsonElement read = await (await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}/destinations/{id}"))).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement authentication = read.GetProperty("authentication");

        HttpResponseMessage resubmitted = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{Fixture.TenantId}/destinations/{id}",
            new
            {
                name = read.GetProperty("name").GetString(),
                configuration = read.GetProperty("configuration"),
                authentication = new
                {
                    scheme = authentication.GetProperty("scheme").GetString(),
                    config = authentication.GetProperty("config"),
                    secret_refs = authentication.GetProperty("secret_refs"),
                },
                environment = (string?)null,
                description = (string?)null,
            }));

        resubmitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement after = await resubmitted.Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("authentication").GetProperty("secret_refs").GetProperty("token").GetString()
            .ShouldBe("round_trip_token");
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
