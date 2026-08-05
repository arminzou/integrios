using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Domain.Events;
using Integrios.Ingress.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Integrios.Tests.Shared;

namespace Integrios.IntegrationTests;

public sealed class EventsAcceptanceBoundaryTests : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    private readonly PostgresApiFixture fixture;
    private readonly string tenantAAuthHeaderValue;
    private readonly string tenantBAuthHeaderValue;
    private HttpClient client = null!;
    private Guid defaultTopicId;
    private Guid defaultSourceConnectionId;

    public EventsAcceptanceBoundaryTests(PostgresApiFixture fixture)
    {
        this.fixture = fixture;
        tenantAAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantAToken}";
        tenantBAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantBToken}";
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
        defaultSourceConnectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantAId, "payments-source");
        defaultTopicId = await fixture.SeedTopicAsync(fixture.TenantAId, "payments");
        await fixture.AssociateSourceAsync(fixture.TenantAId, defaultTopicId, defaultSourceConnectionId);
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

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var eventCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM events;", connection);
        var eventCount = (long)(await eventCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, eventCount);

        await using var outboxCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM outbox;", connection);
        var outboxCount = (long)(await outboxCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, outboxCount);

        Assert.Equal(defaultSourceConnectionId, await fixture.GetEventSourceConnectionIdAsync(body.EventId));
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
    public async Task GetEventsById_ReturnsFannedOutStatus_WhenEventFannedOut()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-fannedout-1");
        var postResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(postBody);

        await fixture.ForceEventStatusAsync(postBody.EventId, "fanned_out");

        var getResponse = await GetEventAsync(postBody.EventId);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getBody = await getResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        Assert.NotNull(getBody);
        Assert.Equal(EventStatus.FannedOut, getBody.Status);

        // Wire format: status is the canonical snake_case string, not the enum's integer.
        var raw = await (await GetEventAsync(postBody.EventId)).Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"fanned_out\"", raw);
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

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var eventCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM events;", connection);
        var eventCount = (long)(await eventCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, eventCount);

        await using var outboxCountCommand = new NpgsqlCommand("SELECT COUNT(*) FROM outbox;", connection);
        var outboxCount = (long)(await outboxCountCommand.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, outboxCount);
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
    public async Task Replay_DeadLetteredDelivery_ViaHttp_Returns202AndResetsDeliveryToPending()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-replay-http");
        var postResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var body = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        Assert.NotNull(body);

        await fixture.ForceDeadLetteredDeliveryAsync(body.EventId);

        var otherTenantReplay = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"/events/{body.EventId}/replay")
        {
            Headers = { { "Authorization", tenantBAuthHeaderValue } }
        });
        Assert.Equal(HttpStatusCode.NotFound, otherTenantReplay.StatusCode);

        await using (var isolationConnection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await isolationConnection.OpenAsync();
            await using var isolationStatus = new NpgsqlCommand(
                "SELECT status FROM subscription_deliveries WHERE event_id = @Id", isolationConnection);
            isolationStatus.Parameters.AddWithValue("Id", body.EventId);
            Assert.Equal("dead_lettered", await isolationStatus.ExecuteScalarAsync());
        }

        var replayResponse = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"/events/{body.EventId}/replay")
        {
            Headers = { { "Authorization", tenantAAuthHeaderValue } }
        });
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var statusCmd = new NpgsqlCommand(
            "SELECT status FROM subscription_deliveries WHERE event_id = @Id", connection);
        statusCmd.Parameters.AddWithValue("Id", body.EventId);
        Assert.Equal("pending", await statusCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PostEvents_WithTopicName_StoresTopicIdOnEvent()
    {
        var request = new IngestEventRequest
        {
            TopicName = "payments",
            SourceConnectionId = defaultSourceConnectionId,
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
            SourceConnectionId = defaultSourceConnectionId,
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
        var request = BuildRequest("idem-unassociated") with { SourceConnectionId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithInactiveSourceConnection_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId, "inactive-source", status: "disabled");
        await fixture.AssociateSourceAsync(fixture.TenantAId, defaultTopicId, sourceConnectionId);
        var request = BuildRequest("idem-inactive") with { SourceConnectionId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithDestinationOnlySourceConnection_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(
            fixture.TenantAId, "destination-only", direction: "destination");
        await fixture.AssociateSourceAsync(fixture.TenantAId, defaultTopicId, sourceConnectionId);
        var request = BuildRequest("idem-destination-only") with { SourceConnectionId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostEvents_WithOtherTenantSourceConnection_Returns422()
    {
        var sourceConnectionId = await fixture.SeedSourceConnectionAsync(fixture.TenantBId, "other-tenant-source");
        var request = BuildRequest("idem-other-tenant") with { SourceConnectionId = sourceConnectionId };

        var response = await PostEventAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private IngestEventRequest BuildRequest(string idempotencyKey)
    {
        return new IngestEventRequest
        {
            SourceEventId = "evt_src_123",
            SourceConnectionId = defaultSourceConnectionId,
            TopicName = "payments",
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"paymentId":"pay_123","amount":1200}""").RootElement.Clone(),
            Metadata = JsonDocument.Parse("""{"source":"integration-tests"}""").RootElement.Clone(),
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
