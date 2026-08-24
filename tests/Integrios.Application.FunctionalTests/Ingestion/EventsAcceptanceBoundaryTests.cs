extern alias IngestionHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Application.FunctionalTests.Ingestion;

public sealed class EventsAcceptanceBoundaryTests : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    private readonly PostgresApiFixture fixture;
    private readonly string tenantAAuthHeaderValue;
    private readonly string tenantBAuthHeaderValue;
    private HttpClient client = null!;
    private Guid defaultTopicId;
    private Guid defaultSourceId;

    public EventsAcceptanceBoundaryTests(PostgresApiFixture fixture)
    {
        this.fixture = fixture;
        tenantAAuthHeaderValue = $"TenantApiKey {PostgresApiFixture.TenantAToken}";
        tenantBAuthHeaderValue = $"TenantApiKey {PostgresApiFixture.TenantBToken}";
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
        Guid connectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "payments-source");
        defaultTopicId = await fixture.SeedTopicAsync(fixture.TenantAId, "payments");
        defaultSourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectionId, defaultTopicId);
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
    public async Task PostEvents_PersistsEventAndOutbox()
    {
        var response = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt_src_123"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(body);
        Assert.False(body.AlreadyAccepted);
        Assert.Equal(EventStatus.Accepted, body.Status);

        Assert.Equal(1, await fixture.GetEventCountAsync());
        Assert.Equal(1, await fixture.GetOutboxCountAsync());

        Assert.Equal(defaultSourceId, await fixture.GetEventSourceIdAsync(body.EventId));
    }

    [Fact]
    public async Task PostEvents_TopicIsDerivedFromSource_NotCallerInput()
    {
        var response = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-topic-derived"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(body);

        Assert.Equal(defaultTopicId, await fixture.GetEventTopicIdAsync(body.EventId));
    }

    [Fact]
    public async Task GetEventsById_ReturnsEvent_WhenEventExists()
    {
        var postResponse = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-read-1"));
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(postBody);

        var getResponse = await GetEventAsync(postBody.EventId);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getBody = await getResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        Assert.NotNull(getBody);
        Assert.Equal(postBody.EventId, getBody.EventId);
        Assert.Equal(EventStatus.Accepted, getBody.Status);
        Assert.NotEqual(default, getBody.AcceptedAt);
    }

    [Fact]
    public async Task GetEventsById_ReturnsRoutedStatus_WhenEventRouted()
    {
        var postResponse = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-fannedout-1"));
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(postBody);

        await fixture.ForceEventStatusAsync(postBody.EventId, "routed");

        var getResponse = await GetEventAsync(postBody.EventId);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getBody = await getResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        Assert.NotNull(getBody);
        Assert.Equal(EventStatus.Routed, getBody.Status);

        // Wire format: status is the canonical snake_case string, not the enum's integer.
        var raw = await (await GetEventAsync(postBody.EventId)).Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"routed\"", raw);
    }

    [Fact]
    public async Task GetEventsById_Returns404_WhenEventDoesNotExist()
    {
        var getResponse = await GetEventAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetEventsById_OtherTenant_Returns404()
    {
        var postResponse = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-tenant-isolation"));
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(postBody);

        var getResponseForTenantB = await GetEventAsync(postBody.EventId, tenantBAuthHeaderValue);
        Assert.Equal(HttpStatusCode.NotFound, getResponseForTenantB.StatusCode);
    }

    [Fact]
    public async Task PostEvents_DuplicateSourceEventId_IsSuppressed()
    {
        var body = BuildBody(sourceEventId: "evt-dup");

        var firstResponse = await PostEventAsync(defaultSourceId, body);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(firstBody);
        Assert.False(firstBody.AlreadyAccepted);

        var secondResponse = await PostEventAsync(defaultSourceId, body);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(secondBody);
        Assert.True(secondBody.AlreadyAccepted);
        Assert.Equal(firstBody.EventId, secondBody.EventId);

        Assert.Equal(1, await fixture.GetEventCountAsync());
        Assert.Equal(1, await fixture.GetOutboxCountAsync());
    }

    [Fact]
    public async Task PostEvents_AttributesEventToAuthenticatedTenant()
    {
        var response = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-tenant-write"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(body);

        var writtenTenantId = await fixture.GetEventTenantIdAsync(body.EventId);
        Assert.Equal(fixture.TenantAId, writtenTenantId);
    }

    [Fact]
    public async Task PostEvents_MissingSourceId_Returns400()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(BuildBody(sourceEventId: "evt-no-source"), options: HostJson.Options)
        };
        message.Headers.TryAddWithoutValidation("Authorization", tenantAAuthHeaderValue);

        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_UnknownSourceId_Returns404()
    {
        var response = await PostEventAsync(Guid.NewGuid(), BuildBody(sourceEventId: "evt-unknown-source"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithInactiveConnection_Returns404()
    {
        var connectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId, "inactive-source", status: "disabled");
        var sourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectionId, defaultTopicId);

        var response = await PostEventAsync(sourceId, BuildBody(sourceEventId: "evt-inactive-connection"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithDestinationOnlyConnection_Returns404()
    {
        var connectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId, "destination-only", direction: "destination");
        var sourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectionId, defaultTopicId);

        var response = await PostEventAsync(sourceId, BuildBody(sourceEventId: "evt-destination-only"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithOtherTenantSourceId_Returns404()
    {
        var connectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantBId, "other-tenant-source");
        var topicId = await fixture.SeedTopicAsync(fixture.TenantBId, "other-tenant-topic");
        var sourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantBId, connectionId, topicId);

        var response = await PostEventAsync(sourceId, BuildBody(sourceEventId: "evt-other-tenant"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_MalformedJson_Returns400()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={defaultSourceId}")
        {
            Content = new StringContent("{not valid json", System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("Authorization", tenantAAuthHeaderValue);

        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_UnsupportedMediaType_Returns415()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={defaultSourceId}")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain")
        };
        message.Headers.TryAddWithoutValidation("Authorization", tenantAAuthHeaderValue);

        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_PassthroughContractWithUnsupportedField_Returns422()
    {
        // topic_name is not an allowed Source-contract output field: a passthrough (no-mapping)
        // contract rejects it outright, so the caller cannot smuggle Topic selection back into
        // the request through the body.
        Guid sourceId = await SeedContractSourceAsync("passthrough_unsupported_field_test", sourceContractHasMapping: false);

        var response = await PostEventAsync(sourceId, new
        {
            event_type = "payment.created",
            topic_name = "payments",
            payload = new { amount = 500 },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_SchemaInvalidInput_Returns422()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"event_type":{"type":"string"}},"required":["event_type"],"additionalProperties":true}""");
        Guid sourceId = await SeedContractSourceAsync(
            "schema_test", sourceContractSchema: schema);

        var response = await PostEventAsync(sourceId, new
        {
            // event_type must be a string per the declared schema; a number violates it.
            event_type = 42,
            payload = new { amount = 500 },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_MappingEvaluationFailure_Returns422()
    {
        Guid sourceId = await SeedContractSourceAsync(
            "mapping_failure_test", sourceMappingExpression: "$error(\"boom\")");

        var response = await PostEventAsync(sourceId, BuildBody(sourceEventId: "evt-mapping-failure"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_PassthroughContractMissingEventType_Returns422()
    {
        Guid sourceId = await SeedContractSourceAsync("passthrough_test", sourceContractHasMapping: false);

        var response = await PostEventAsync(sourceId, new { payload = new { amount = 500 } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private async Task<Guid> SeedContractSourceAsync(
        string connectorKey,
        bool sourceContractHasMapping = true,
        string sourceMappingExpression = "{ \"event_type\": event_type, \"source_event_id\": source_event_id, \"payload\": payload, \"metadata\": metadata }",
        JsonElement? sourceContractSchema = null)
    {
        string manifest = TestConnectorManifest.Create(
            connectorKey, connectorKey, "source",
            declarativeSourceContract: true,
            sourceContractHasMapping: sourceContractHasMapping,
            sourceMappingExpression: sourceMappingExpression,
            sourceContractSchema: sourceContractSchema);
        Guid connectionId = await fixture.SeedConnectorConnectionAsync(fixture.TenantAId, connectorKey, manifest);

        return await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectionId, defaultTopicId);
    }

    private static object BuildBody(string? sourceEventId) => new
    {
        event_type = "payment.created",
        source_event_id = sourceEventId,
        payload = new { paymentId = "pay_123", amount = 1200 },
        metadata = new { source = "connector-tests" },
    };

    private Task<HttpResponseMessage> PostEventAsync(Guid sourceId, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={sourceId}")
        {
            Content = JsonContent.Create(body, options: HostJson.Options)
        };
        message.Headers.TryAddWithoutValidation("Authorization", tenantAAuthHeaderValue);
        return client.SendAsync(message);
    }

    private Task<HttpResponseMessage> GetEventAsync(Guid eventId, string? authHeader = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, $"/events/{eventId}");
        message.Headers.TryAddWithoutValidation(
            "Authorization",
            authHeader ?? tenantAAuthHeaderValue);
        return client.SendAsync(message);
    }
}
