extern alias IngressHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using IngressHost::Integrios.Ingress.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Application.FunctionalTests.Ingress;

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
        tenantAAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantAToken}";
        tenantBAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantBToken}";
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
        Guid sourceConnectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "payments-source");
        defaultTopicId = await fixture.SeedTopicAsync(fixture.TenantAId, "payments");
        defaultSourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, sourceConnectionId, defaultTopicId);
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
        var request = BuildRequest(idempotencyKey: "idem-evt-1");

        var response = await PostEventAsync(request);
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
    public async Task GetEventsById_ReturnsEvent_WhenEventExists()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-read-1");
        var postResponse = await PostEventAsync(request);
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
        var request = BuildRequest(idempotencyKey: "idem-evt-fannedout-1");
        var postResponse = await PostEventAsync(request);
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
        var request = BuildRequest(idempotencyKey: "idem-evt-tenant-isolation");
        var postResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(postBody);

        var getResponseForTenantB = await GetEventAsync(postBody.EventId, tenantBAuthHeaderValue);
        Assert.Equal(HttpStatusCode.NotFound, getResponseForTenantB.StatusCode);
    }

    [Fact]
    public async Task PostEvents_DuplicateIdempotencyKey_IsSuppressed()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-dup");

        var firstResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(firstBody);
        Assert.False(firstBody.AlreadyAccepted);

        var secondResponse = await PostEventAsync(request);
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
        var request = BuildRequest(idempotencyKey: "idem-evt-tenant-write");

        var response = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(body);

        var writtenTenantId = await fixture.GetEventTenantIdAsync(body.EventId);
        Assert.Equal(fixture.TenantAId, writtenTenantId);
    }

    [Fact]
    public async Task PostEvents_WithTopicName_StoresTopicIdOnEvent()
    {
        var request = new IngestEventRequest
        {
            TopicName = "payments",
            SourceId = defaultSourceId,
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"amount":500}""").RootElement.Clone(),
        };

        var response = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(body);

        var storedTopicId = await fixture.GetEventTopicIdAsync(body.EventId);
        Assert.Equal(defaultTopicId, storedTopicId);
    }

    [Fact]
    public async Task PostEvents_WithUnknownTopicName_Returns422()
    {
        var request = new IngestEventRequest
        {
            TopicName = "nonexistent-topic",
            SourceId = defaultSourceId,
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"amount":500}""").RootElement.Clone(),
        };

        var response = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithUnassociatedSourceConnection_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "unassociated-source");
        var request = BuildRequest("idem-unassociated") with { SourceId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithInactiveSourceConnection_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId, "inactive-source", status: "disabled");
        await fixture.AssociateSourceAsync(fixture.TenantAId, defaultTopicId, sourceConnectionId);
        var request = BuildRequest("idem-inactive") with { SourceId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithDestinationOnlySourceConnection_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId, "destination-only", direction: "destination");
        await fixture.AssociateSourceAsync(fixture.TenantAId, defaultTopicId, sourceConnectionId);
        var request = BuildRequest("idem-destination-only") with { SourceId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithOtherTenantSourceConnection_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantBId, "other-tenant-source");
        var request = BuildRequest("idem-other-tenant") with { SourceId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithRetiredSourceAssociation_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "retired-source");
        await fixture.AssociateSourceAsync(fixture.TenantAId, defaultTopicId, sourceConnectionId);
        await fixture.RetireSourceAsync(fixture.TenantAId, defaultTopicId, sourceConnectionId);
        var request = BuildRequest("idem-retired") with { SourceId = sourceConnectionId };

        var response = await PostEventAsync(request);

        // A retired association is a tombstone, so intake must reject it the same way it rejects
        // one that never existed — not resolve the Topic and fail on the database trigger.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private IngestEventRequest BuildRequest(string idempotencyKey)
    {
        return new IngestEventRequest
        {
            SourceEventId = "evt_src_123",
            SourceId = defaultSourceId,
            TopicName = "payments",
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"paymentId":"pay_123","amount":1200}""").RootElement.Clone(),
            Metadata = JsonDocument.Parse("""{"source":"connector-tests"}""").RootElement.Clone(),
            IdempotencyKey = idempotencyKey
        };
    }

    private Task<HttpResponseMessage> PostEventAsync(IngestEventRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(request, options: HostJson.Options)
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
