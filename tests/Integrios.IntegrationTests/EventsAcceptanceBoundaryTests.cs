using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Domain.Events;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Integrios.IntegrationTests;

public sealed class EventsAcceptanceBoundaryTests : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    private readonly PostgresApiFixture fixture;
    private readonly string tenantAAuthHeaderValue;
    private readonly string tenantBAuthHeaderValue;
    private HttpClient client = null!;

    public EventsAcceptanceBoundaryTests(PostgresApiFixture fixture)
    {
        this.fixture = fixture;
        tenantAAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantAToken}";
        tenantBAuthHeaderValue = $"ApiKey {PostgresApiFixture.TenantBToken}";
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
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

        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(body);
        Assert.False(body.IsDuplicate);
        Assert.Equal(EventStatus.Accepted, body.Status);

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
    public async Task GetEventsById_ReturnsEvent_WhenEventExists()
    {
        var request = BuildRequest(idempotencyKey: "idem-evt-read-1");
        var postResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(postBody);

        var getResponse = await GetEventAsync(postBody.EventId);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var getBody = await getResponse.Content.ReadFromJsonAsync<GetEventResponse>();
        Assert.NotNull(getBody);
        Assert.Equal(postBody.EventId, getBody.EventId);
        Assert.Equal(EventStatus.Accepted, getBody.Status);
        Assert.NotEqual(default, getBody.AcceptedAt);
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

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
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
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(firstBody);
        Assert.False(firstBody.IsDuplicate);

        var secondResponse = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(secondBody);
        Assert.True(secondBody.IsDuplicate);
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

        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
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

        var body = await postResponse.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(body);

        await fixture.ForceDeadLetteredDeliveryAsync(body.EventId);

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
        var topicId = await fixture.SeedTopicAsync(fixture.TenantAId, "payments");

        var request = new IngestEventRequest
        {
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"amount":500}""").RootElement.Clone(),
            TopicName = "payments"
        };

        var response = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(body);

        var storedTopicId = await fixture.GetEventTopicIdAsync(body.EventId);
        Assert.Equal(topicId, storedTopicId);
    }

    [Fact]
    public async Task PostEvents_WithUnknownTopicName_StoresNullTopicId()
    {
        var request = new IngestEventRequest
        {
            EventType = "payment.created",
            Payload = JsonDocument.Parse("""{"amount":500}""").RootElement.Clone(),
            TopicName = "nonexistent-topic"
        };

        var response = await PostEventAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        Assert.NotNull(body);

        var storedTopicId = await fixture.GetEventTopicIdAsync(body.EventId);
        Assert.Null(storedTopicId);
    }

    private static IngestEventRequest BuildRequest(string idempotencyKey)
    {
        return new IngestEventRequest
        {
            SourceEventId = "evt_src_123",
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
            Content = JsonContent.Create(request)
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
