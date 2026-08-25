using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Application.FunctionalTests.Admin;

public sealed class SubscriptionsAdminTests : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{

    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public SubscriptionsAdminTests(AdminApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateSubscription_ReturnsCreated_WithCorrectBody()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
        body.TenantId.ShouldBe(fixture.TenantId);
        body.Name.ShouldBe("erp-sink");
        body.DestinationConnectionId.ShouldBe(fixture.SourceConnectionId);
        body.OrderIndex.ShouldBe(10);
        body.Status.ShouldBe("active");
        body.MatchRules.GetProperty("event_type").GetString().ShouldBe("payment.created");
    }

    [Fact]
    public async Task CreateSubscription_WithDeactivatedConnection_ReturnsUnprocessableEntity()
    {
        var topic = await CreateTopicAsync("deactivated-destination");
        await SetConnectionStatusAsync(fixture.SourceConnectionId, "disabled");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "disabled-destination",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 0
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task GetSubscriptionById_ReturnsSubscription()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}"));

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
        await CreateSubscriptionAsync(topic.Id, "sub-c", "payment.failed", orderIndex: 3);

        var page1 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2"));
        page1.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body1 = await page1.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        body1.ShouldNotBeNull();
        body1.Items.Count.ShouldBe(2);
        body1.NextCursor.ShouldNotBeNull();

        var page2 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Items.Count.ShouldBe(2);
        body.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"event_types\":[\"payment.created\"]}")]
    [InlineData("{\"event_type\":123}")]
    [InlineData("{\"event_type\":\"\"}")]
    [InlineData("{\"event_type\":\"payment.created\",\"foo\":\"bar\"}")]
    public async Task CreateSubscription_WithInvalidMatchRules_ReturnsUnprocessableEntity(string matchRulesJson)
    {
        var topic = await CreateTopicAsync("payments");

        using var request = AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = JsonDocument.Parse(matchRulesJson).RootElement,
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 10,
                description = "Primary ERP delivery"
            });

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"event_types\":[\"payment.updated\"]}")]
    [InlineData("{\"event_type\":null}")]
    [InlineData("{\"event_type\":\"   \"}")]
    [InlineData("{\"event_type\":\"payment.updated\",\"foo\":true}")]
    public async Task UpdateSubscription_WithInvalidMatchRules_ReturnsUnprocessableEntity(string matchRulesJson)
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        using var request = AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                match_rules = JsonDocument.Parse(matchRulesJson).RootElement,
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 25,
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
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                match_rules = new { event_type = "payment.updated" },
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 25,
                description = "Updated ERP delivery"
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("erp-sink-v2");
        body.OrderIndex.ShouldBe(25);
        body.MatchRules.GetProperty("event_type").GetString().ShouldBe("payment.updated");
    }

    [Fact]
    public async Task DeactivateSubscription_Returns200_AndStatusBecomesDisabled()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var deactivate = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}/deactivate"));
        deactivate.StatusCode.ShouldBe(HttpStatusCode.OK);

        var get = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}"));
        get.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await get.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("disabled");
    }

    private async Task<AdminTopicResponse> CreateTopicAsync(string name)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name }));

        response.EnsureSuccessStatusCode();
        var topic = await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options);
        return topic!;
    }

    private async Task<SubscriptionDto> CreateSubscriptionAsync(
        Guid topicId,
        string name,
        string eventType,
        int orderIndex = 10,
        string? description = null,
        JsonElement? mapping = null)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions",
            new
            {
                name,
                match_rules = new { event_type = eventType },
                destination_connection_id = fixture.SourceConnectionId,
                order_index = orderIndex,
                description,
                mapping
            }));

        response.EnsureSuccessStatusCode();
        var subscription = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        return subscription!;
    }

    private async Task SetConnectionStatusAsync(Guid connectionId, string status)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE connections SET status = @Status WHERE id = @Id",
            new { Status = status, Id = connectionId });
    }

    [Fact]
    public async Task CreateSubscription_WithValidTransform_ReturnsTransformInResponse()
    {
        var topic = await CreateTopicAsync("payments");
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.amount"}""";
        var transformElement = JsonDocument.Parse(transformJson).RootElement;

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
    public async Task CreateSubscription_WithNullTransform_Succeeds()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions",
            new
            {
                name = "oversized-transform",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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

    [Fact]
    public async Task UpdateSubscription_WhenDisabled_ReturnsNotFound()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}/deactivate"));

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 10
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeactivateSubscription_AlreadyDisabled_ReturnsNotFound()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}/deactivate"));

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}/deactivate"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
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
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.OtherTenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink-v2",
                match_rules = new { event_type = "payment.updated" },
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 2
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSubscription_WhenSubscriptionDoesNotExist_ReturnsNotFoundBeforeDestinationValidation()
    {
        var topic = await CreateTopicAsync("payments");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{Guid.NewGuid()}",
            new
            {
                name = "missing-subscription",
                match_rules = new { event_type = "payment.updated" },
                destination_connection_id = Guid.NewGuid(),
                order_index = 2
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed record SubscriptionDto(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        JsonElement? MappingConfig,
        string Status,
        int OrderIndex,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record SubscriptionListDto(
        IReadOnlyList<SubscriptionDto> Items,
        string? NextCursor);
}
