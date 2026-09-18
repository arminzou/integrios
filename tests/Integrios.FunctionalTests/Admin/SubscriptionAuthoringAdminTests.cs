using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class SubscriptionAuthoringAdminTests : SubscriptionAdminTestBase
{
    public SubscriptionAuthoringAdminTests(AdminApiFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task CreateSubscription_ReturnsCreated_WithCorrectBody()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                event_types = new[] { "payment.created" },
                destination_id = Fixture.DestinationId,
                order_index = 10,
                description = "Primary ERP delivery"
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var responseJson = await response.Content.ReadAsStringAsync();
        using var responseDocument = JsonDocument.Parse(responseJson);
        responseDocument.RootElement.TryGetProperty("dlq_enabled", out _).ShouldBeFalse();

        var body = JsonSerializer.Deserialize<SubscriptionDto>(responseJson, HostJson.Options);
        body.ShouldNotBeNull();
        body.TopicId.ShouldBe(topic.Id);
        body.TenantId.ShouldBe(Fixture.TenantId);
        body.Name.ShouldBe("erp-sink");
        body.DestinationId.ShouldBe(Fixture.DestinationId);
        body.OrderIndex.ShouldBe(10);
        // Authorable before it routes anything: fanout ignores it until it is enabled.
        body.Status.ShouldBe("disabled");
        body.EventTypes.ShouldBe(["payment.created"]);
    }

    [Fact]
    public async Task CreateSubscription_OnADisabledDestination_IsAuthoredDisabled()
    {
        var topic = await CreateTopicAsync("deactivated-destination");
        await SetDestinationStatusAsync(Fixture.DestinationId, "disabled");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "disabled-destination",
                event_types = new[] { "payment.created" },
                destination_id = Fixture.DestinationId,
                order_index = 0
            }));

        // A Disabled Subscription may point at a Disabled Destination; neither creates work.
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options))!.Status.ShouldBe("disabled");
    }

    [Fact]
    public async Task GetSubscriptionById_ReturnsSubscription()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Id.ShouldBe(created.Id);
        body.Name.ShouldBe("erp-sink");
    }

    [Fact]
    public async Task ListSubscriptions_ReturnsCursorPaginatedResults()
    {
        var topic = await CreateTopicAsync("payments");
        await CreateSubscriptionAsync(topic.Id, "sub-a", "payment.created", orderIndex: 1);
        await CreateSubscriptionAsync(topic.Id, "sub-b", "payment.updated", orderIndex: 2);
        await CreateSubscriptionAsync(topic.Id, "sub-c", "payment.created", orderIndex: 3);

        var page1 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2"));
        page1.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body1 = await page1.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        body1.ShouldNotBeNull();
        body1.Items.Count.ShouldBe(2);
        body1.NextCursor.ShouldNotBeNull();

        var page2 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        page2.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body2 = await page2.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        body2.ShouldNotBeNull();
        body2.Items.ShouldHaveSingleItem();
        body2.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task ListSubscriptions_ExactPageHasNoNextCursor()
    {
        var topic = await CreateTopicAsync("payments");
        await CreateSubscriptionAsync(topic.Id, "sub-a", "payment.created", orderIndex: 1);
        await CreateSubscriptionAsync(topic.Id, "sub-b", "payment.updated", orderIndex: 2);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Items.Count.ShouldBe(2);
        body.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"payment.created \"]")]
    [InlineData("[\"payment\\tcreated\"]")]
    [InlineData("[\"payment.created\",\"PAYMENT.CREATED\"]")]
    [InlineData("[\"payment.refunded\"]")]
    public async Task CreateSubscription_WithInvalidEventTypes_IsRefused(string eventTypesJson)
    {
        var topic = await CreateTopicAsync("payments");

        using var request = AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                event_types = JsonDocument.Parse(eventTypesJson).RootElement,
                destination_id = Fixture.DestinationId,
                order_index = 10,
                description = "Primary ERP delivery"
            });

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[\"   \"]")]
    [InlineData("[\" payment.updated\"]")]
    [InlineData("[\"payment\\nupdated\"]")]
    [InlineData("[\"payment.refunded\"]")]
    public async Task UpdateSubscription_WithInvalidEventTypes_IsRefused(string eventTypesJson)
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        using var request = AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                event_types = JsonDocument.Parse(eventTypesJson).RootElement,
                destination_id = Fixture.DestinationId,
                order_index = 25,
                mapping = (object?)null,
                http_delivery = (object?)null,
                http_success = (object?)null,
                description = "Updated ERP delivery"
            });

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateSubscription_UpdatesEditableFields()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                event_types = new[] { "payment.updated" },
                destination_id = Fixture.DestinationId,
                order_index = 25,
                mapping = (object?)null,
                http_delivery = (object?)null,
                http_success = (object?)null,
                description = "Updated ERP delivery"
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("erp-sink-v2");
        body.OrderIndex.ShouldBe(25);
        body.EventTypes.ShouldBe(["payment.updated"]);
    }

    [Fact]
    public async Task EnableAndDisable_AreExplicitReversibleActions()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");
        string path = $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}";

        foreach ((string action, string expected) in new[] { ("enable", "enabled"), ("enable", "enabled"), ("disable", "disabled"), ("enable", "enabled") })
        {
            var response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"{path}/{action}"));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options))!.Status.ShouldBe(expected);
        }

        (await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{Guid.NewGuid()}/enable")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Disabling pauses routing; it does not freeze the configuration, so the Operator can fix what
    // made them pause it before enabling it again.
    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task UpdateSubscription_EditsEitherStatus(string action)
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");
        await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}/{action}"));

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-renamed",
                event_types = new[] { "payment.updated" },
                destination_id = Fixture.DestinationId,
                order_index = 10,
                mapping = (object?)null,
                http_delivery = (object?)null,
                http_success = (object?)null,
                description = (string?)null,
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options))!;
        body.Name.ShouldBe("erp-sink-renamed");
        body.Status.ShouldBe(action == "enable" ? "enabled" : "disabled");
    }

    [Fact]
    public async Task CreateSubscription_ForTopicOwnedByAnotherTenant_ReturnsNotFound()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Guid.NewGuid()}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                event_types = new[] { "payment.created" },
                destination_id = Fixture.DestinationId,
                order_index = 1
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSubscription_ForTopicOwnedByAnotherTenant_ReturnsNotFound()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{Fixture.OtherTenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                event_types = new[] { "payment.updated" },
                destination_id = Fixture.DestinationId,
                order_index = 2,
                mapping = (object?)null,
                http_delivery = (object?)null,
                http_success = (object?)null,
                description = (string?)null,
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSubscription_WhenSubscriptionDoesNotExist_ReturnsNotFoundBeforeDestinationValidation()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{Guid.NewGuid()}",
            new
            {
                name = "missing-subscription",
                event_types = new[] { "payment.updated" },
                destination_id = Guid.NewGuid(),
                order_index = 2,
                mapping = (object?)null,
                http_delivery = (object?)null,
                http_success = (object?)null,
                description = (string?)null,
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
