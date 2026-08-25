using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Tests.Shared;

namespace Integrios.FunctionalTests.Admin;

public sealed class SubscriptionTransformAdminTests : SubscriptionAdminTestBase
{
    public SubscriptionTransformAdminTests(AdminApiFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task CreateSubscription_WithValidTransform_ReturnsTransformInResponse()
    {
        var topic = await CreateTopicAsync("payments");
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.amount"}""";
        var transformElement = JsonDocument.Parse(transformJson).RootElement;

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 1,
                mapping = transformElement
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.MappingConfig.ShouldNotBeNull();
        body.MappingConfig.Value.GetProperty("engine").GetString().ShouldBe("jsonata");
        body.MappingConfig.Value.GetProperty("version").GetString().ShouldBe("1");
        body.MappingConfig.Value.GetProperty("expression").GetString().ShouldBe("$.amount");
    }

    [Fact]
    public async Task CreateSubscription_WithNullTransform_ReturnsCreatedWithNoMapping()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 1,
                mapping = (object?)null
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.MappingConfig.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateSubscription_AddsTransform_ReturnsUpdatedTransform()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.amount * 2"}""";
        var transformElement = JsonDocument.Parse(transformJson).RootElement;

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 10,
                mapping = transformElement
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.MappingConfig.ShouldNotBeNull();
        body.MappingConfig.Value.GetProperty("expression").GetString().ShouldBe("$.amount * 2");
    }

    [Fact]
    public async Task UpdateSubscription_RemovesTransform_TransformBecomesNull()
    {
        var topic = await CreateTopicAsync("payments");
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.amount"}""";
        var transformElement = JsonDocument.Parse(transformJson).RootElement;
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created", mapping: transformElement);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 10,
                mapping = (object?)null
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.MappingConfig.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"engine":"unknown","version":"1","expression":"$.amount"}""")]
    public async Task CreateSubscription_WithTransform_InvalidEngine_ReturnsUnprocessableEntity(string transformJson)
    {
        var topic = await CreateTopicAsync("payments");
        var transformElement = JsonDocument.Parse(transformJson).RootElement;

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 1,
                mapping = transformElement
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("""{"engine":"jsonata","version":"99","expression":"$.amount"}""")]
    public async Task CreateSubscription_WithTransform_InvalidVersion_ReturnsUnprocessableEntity(string transformJson)
    {
        var topic = await CreateTopicAsync("payments");
        var transformElement = JsonDocument.Parse(transformJson).RootElement;

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 1,
                mapping = transformElement
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("""{"engine":"jsonata","version":"1","expression":"$.[invalid"}""")]
    public async Task CreateSubscription_WithTransform_InvalidJsonataExpression_ReturnsUnprocessableEntity(string transformJson)
    {
        var topic = await CreateTopicAsync("payments");
        var transformElement = JsonDocument.Parse(transformJson).RootElement;

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 1,
                mapping = transformElement
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateSubscription_WithTransform_MissingExpression_ReturnsUnprocessableEntity()
    {
        var topic = await CreateTopicAsync("payments");
        var transformElement = JsonDocument.Parse("""{"engine":"jsonata","version":"1"}""").RootElement;

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 1,
                mapping = transformElement
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateSubscription_WithTransformLargerThan64KiB_ReturnsUnprocessableEntity()
    {
        var topic = await CreateTopicAsync("payments");
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "oversized-transform",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = Fixture.SourceConnectionId,
                order_index = 10,
                mapping = new
                {
                    engine = "jsonata",
                    version = "1",
                    expression = new string('x', 65537)
                }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("64 KiB", Case.Sensitive);
    }

    [Fact]
    public async Task PreviewMapping_InvalidConfig_ReturnsBadRequestWithErrorBody()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/transform/preview",
            new
            {
                transform = new { engine = "jsonata", version = "1" },
                sample_input = new { amount = 42 }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options);
        body.GetProperty("error").GetString()!.ShouldContain("expression", Case.Sensitive);
    }

    [Fact]
    public async Task PreviewMapping_EvaluationFailure_ReturnsBadRequestWithErrorBody()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/transform/preview",
            new
            {
                mapping = new
                {
                    engine = "jsonata",
                    version = "1",
                    expression = "amount + $context.event_type"
                },
                sample_input = new { amount = 42 }
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options);
        string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()).ShouldBeFalse();
    }
}
