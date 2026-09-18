using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Admin.Endpoints;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

// A Topic exposes what its Sources declare, and Subscriptions choose from that and nothing else. These
// pin the two directions the rule is enforced in: Subscriptions cannot reach outside the union, and
// Sources cannot pull a type out from under a Subscription or re-spell one the Topic already shows.
public sealed class TopicEventTypesAdminTests(AdminApiFixture fixture) : SubscriptionAdminTestBase(fixture)
{
    [Fact]
    public async Task Topic_ExposesTheUnionOfItsSourcesDeclarations()
    {
        AdminTopicResponse topic = await CreateBareTopicAsync("orders");
        AdminTopicResponse other = await CreateBareTopicAsync("invoices");
        (await CreateSourceAsync(topic.Id, "order.shipped", "Order.Created")).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CreateSourceAsync(topic.Id, "Order.Created", "order.cancelled")).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CreateSourceAsync(other.Id, "invoice.issued")).StatusCode.ShouldBe(HttpStatusCode.Created);

        AdminTopicResponse read = await GetTopicAsync(topic.Id);

        read.EventTypes.ShouldBe(["order.cancelled", "Order.Created", "order.shipped"]);
        (await ListTopicsAsync()).Single(listed => listed.Id == topic.Id).EventTypes.ShouldBe(read.EventTypes);
    }

    [Fact]
    public async Task SourceAuthoring_RefusesASecondSpellingOfAnExposedType()
    {
        AdminTopicResponse topic = await CreateBareTopicAsync("orders");
        (await CreateSourceAsync(topic.Id, "Order.Created")).StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage response = await CreateSourceAsync(topic.Id, "order.created");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await GetTopicAsync(topic.Id)).EventTypes.ShouldBe(["Order.Created"]);
    }

    [Fact]
    public async Task Subscription_SelectsSeveralTypesFromTheUnion()
    {
        AdminTopicResponse topic = await CreateBareTopicAsync("orders");
        await CreateSourceAsync(topic.Id, "order.created", "order.shipped");

        HttpResponseMessage response = await CreateSubscriptionAsync(topic.Id, "order.shipped", "ORDER.CREATED");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("event_types").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["order.shipped", "order.created"]);
    }

    [Fact]
    public async Task SourceUpdate_RefusesToWithdrawTheLastDeclarationASubscriptionSelects()
    {
        AdminTopicResponse topic = await CreateBareTopicAsync("orders");
        Guid source = await CreatedIdAsync(await CreateSourceAsync(topic.Id, "order.created", "order.shipped"));
        (await CreateSubscriptionAsync(topic.Id, "order.shipped")).StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage refused = await UpdateSourceAsync(source, "order.created");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("order.shipped");
        (await GetTopicAsync(topic.Id)).EventTypes.ShouldBe(["order.created", "order.shipped"]);
    }

    [Fact]
    public async Task SourceUpdate_WithdrawsATypeAnotherSourceStillDeclares()
    {
        AdminTopicResponse topic = await CreateBareTopicAsync("orders");
        Guid source = await CreatedIdAsync(await CreateSourceAsync(topic.Id, "order.created", "order.shipped"));
        await CreateSourceAsync(topic.Id, "order.shipped");
        (await CreateSubscriptionAsync(topic.Id, "order.shipped")).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await UpdateSourceAsync(source, "order.created")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<AdminTopicResponse> CreateBareTopicAsync(string key)
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/topics", new { key }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;
    }

    private async Task<AdminTopicResponse> GetTopicAsync(Guid topicId) =>
        (await (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}/topics/{topicId}")))
            .Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;

    private async Task<IReadOnlyList<AdminTopicResponse>> ListTopicsAsync() =>
        (await (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}/topics")))
            .Content.ReadFromJsonAsync<AdminTopicListResponse>(HostJson.Options))!.Items;

    private Task<HttpResponseMessage> CreateSourceAsync(Guid topicId, params string[] eventTypes) =>
        client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/sources", new
        {
            connector_id = Fixture.HttpConnectorId,
            topic_id = topicId,
            name = "event-api-intake",
            type = "event_api",
            event_types = eventTypes,
            configuration = new { },
        }));

    private Task<HttpResponseMessage> UpdateSourceAsync(Guid sourceId, params string[] eventTypes) =>
        client.SendAsync(AdminRequest(HttpMethod.Put, $"/admin/tenants/{Fixture.TenantId}/sources/{sourceId}", new
        {
            name = "event-api-intake",
            configuration = new { },
            verification = (object?)null,
            input_requirements = (object?)null,
            mapping = (object?)null,
            event_identity_rule = (object?)null,
            event_types = eventTypes,
        }));

    private Task<HttpResponseMessage> CreateSubscriptionAsync(Guid topicId, params string[] eventTypes) =>
        client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/topics/{topicId}/subscriptions", new
        {
            name = "orders-to-erp",
            event_types = eventTypes,
            destination_id = Fixture.DestinationId,
            order_index = 0,
        }));

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }
}
