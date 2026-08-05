using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Admin.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Integrios.Tests.Shared;

namespace Integrios.Admin.Tests;

public sealed class SubscriptionsAdminTests : IClassFixture<AdminApiFixture>, IAsyncLifetime
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

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync();
        using var responseDocument = JsonDocument.Parse(responseJson);
        Assert.False(responseDocument.RootElement.TryGetProperty("dlq_enabled", out _));

        var body = JsonSerializer.Deserialize<SubscriptionDto>(responseJson, HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal(topic.Id, body.TopicId);
        Assert.Equal(fixture.TenantId, body.TenantId);
        Assert.Equal("erp-sink", body.Name);
        Assert.Equal(fixture.SourceConnectionId, body.DestinationConnectionId);
        Assert.Equal(10, body.OrderIndex);
        Assert.Equal("active", body.Status);
        Assert.Equal("payment.created", body.MatchRules.GetProperty("event_type").GetString());
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

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task GetSubscriptionById_ReturnsSubscription()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Equal("erp-sink", body.Name);
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
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);

        var body1 = await page1.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        Assert.NotNull(body1);
        Assert.Equal(2, body1.Items.Count);
        Assert.NotNull(body1.NextCursor);

        var page2 = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions?limit=2&after={Uri.EscapeDataString(body1.NextCursor!)}"));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);

        var body2 = await page2.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        Assert.NotNull(body2);
        Assert.Single(body2.Items);
        Assert.Null(body2.NextCursor);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SubscriptionListDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal(2, body.Items.Count);
        Assert.Null(body.NextCursor);
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

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal("erp-sink-v2", body.Name);
        Assert.Equal(25, body.OrderIndex);
        Assert.Equal("payment.updated", body.MatchRules.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task DeactivateSubscription_Returns200_AndStatusBecomesDisabled()
    {
        var topic = await CreateTopicAsync("payments");
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created");

        var deactivate = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var get = await client.SendAsync(AdminRequest(
            HttpMethod.Get,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal("disabled", body.Status);
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
        JsonElement? transform = null)
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics/{topicId}/subscriptions",
            new
            {
                name,
                match_rules = new { event_type = eventType },
                destination_connection_id = fixture.SourceConnectionId,
                orderIndex,
                description,
                transform
            }));

        response.EnsureSuccessStatusCode();
        var subscription = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        return subscription!;
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.TryAddWithoutValidation("Authorization", AdminApiFixture.GlobalAdminAuthHeader);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
        return msg;
    }

    private async Task SetConnectionStatusAsync(Guid connectionId, string status)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE connections SET status = @Status WHERE id = @Id",
            connection);
        command.Parameters.AddWithValue("Status", status);
        command.Parameters.AddWithValue("Id", connectionId);
        await command.ExecuteNonQueryAsync();
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
                transform = transformElement
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.NotNull(body.TransformConfig);
        Assert.Equal("jsonata", body.TransformConfig.Value.GetProperty("engine").GetString());
        Assert.Equal("1", body.TransformConfig.Value.GetProperty("version").GetString());
        Assert.Equal("$.amount", body.TransformConfig.Value.GetProperty("expression").GetString());
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
                transform = (object?)null
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Null(body.TransformConfig);
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
                transform = transformElement
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.NotNull(body.TransformConfig);
        Assert.Equal("$.amount * 2", body.TransformConfig.Value.GetProperty("expression").GetString());
    }

    [Fact]
    public async Task UpdateSubscription_RemovesTransform_TransformBecomesNull()
    {
        var topic = await CreateTopicAsync("payments");
        var transformJson = """{"engine":"jsonata","version":"1","expression":"$.amount"}""";
        var transformElement = JsonDocument.Parse(transformJson).RootElement;
        var created = await CreateSubscriptionAsync(topic.Id, "erp-sink", "payment.created", transform: transformElement);

        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Patch,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}/subscriptions/{created.Id}",
            new
            {
                name = "erp-sink",
                match_rules = new { event_type = "payment.created" },
                destination_connection_id = fixture.SourceConnectionId,
                order_index = 10,
                transform = (object?)null
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SubscriptionDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Null(body.TransformConfig);
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
                transform = transformElement
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                transform = transformElement
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                transform = transformElement
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                transform = transformElement
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
                transform = new
                {
                    engine = "jsonata",
                    version = "1",
                    expression = new string('x', 65537)
                }
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("64 KiB", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewTransform_InvalidConfig_ReturnsBadRequestWithErrorBody()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/transform/preview",
            new
            {
                transform = new { engine = "jsonata", version = "1" },
                sample_input = new { amount = 42 }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options);
        Assert.Contains("expression", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PreviewTransform_EvaluationFailure_ReturnsBadRequestWithErrorBody()
    {
        var response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            "/admin/transform/preview",
            new
            {
                transform = new
                {
                    engine = "jsonata",
                    version = "1",
                    expression = "amount + $context.event_type"
                },
                sample_input = new { amount = 42 }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(HostJson.Options);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record SubscriptionDto(
        Guid Id,
        Guid TopicId,
        Guid TenantId,
        string Name,
        JsonElement MatchRules,
        Guid DestinationConnectionId,
        JsonElement? TransformConfig,
        string Status,
        int OrderIndex,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record SubscriptionListDto(
        IReadOnlyList<SubscriptionDto> Items,
        string? NextCursor);
}
