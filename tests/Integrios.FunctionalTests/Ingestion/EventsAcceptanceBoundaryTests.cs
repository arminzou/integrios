extern alias IngestionHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Ingestion;

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
        tenantAAuthHeaderValue = $"Bearer {PostgresApiFixture.TenantAToken}";
        tenantBAuthHeaderValue = $"Bearer {PostgresApiFixture.TenantBToken}";
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
        Guid connectorId = await fixture.SeedSourceConnectorAsync(fixture.TenantAId, "payments-source");
        defaultTopicId = await fixture.SeedTopicAsync(fixture.TenantAId, "payments");
        defaultSourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectorId, defaultTopicId);
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
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location.ShouldNotBeNull();

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        body.ShouldNotBeNull();
        body.AlreadyAccepted.ShouldBeFalse();
        body.Status.ShouldBe(EventStatus.Accepted);

        (await fixture.GetEventCountAsync()).ShouldBe(1);
        (await fixture.GetOutboxCountAsync()).ShouldBe(1);

        (await fixture.GetEventSourceIdAsync(body.EventId)).ShouldBe(defaultSourceId);
    }

    [Fact]
    public async Task PostEvents_TopicIsDerivedFromSource_NotCallerInput()
    {
        var response = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-topic-derived"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        body.ShouldNotBeNull();

        (await fixture.GetEventTopicIdAsync(body.EventId)).ShouldBe(defaultTopicId);
    }

    [Fact]
    public async Task GetEventsById_ReturnsEvent_WhenEventExists()
    {
        var postResponse = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-read-1"));
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        postBody.ShouldNotBeNull();

        var getResponse = await GetEventAsync(postBody.EventId);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getBody = await getResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        getBody.ShouldNotBeNull();
        getBody.EventId.ShouldBe(postBody.EventId);
        getBody.Status.ShouldBe(EventStatus.Accepted);
        getBody.AcceptedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task GetEventsById_ReturnsRoutedStatus_WhenEventRouted()
    {
        var postResponse = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-fannedout-1"));
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        postBody.ShouldNotBeNull();

        await fixture.ForceEventStatusAsync(postBody.EventId, "routed");

        var getResponse = await GetEventAsync(postBody.EventId);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getBody = await getResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        getBody.ShouldNotBeNull();
        getBody.Status.ShouldBe(EventStatus.Routed);

        // Wire format: status is the canonical snake_case string, not the enum's integer.
        var raw = await (await GetEventAsync(postBody.EventId)).Content.ReadAsStringAsync();
        raw.ShouldContain("\"status\":\"routed\"", Case.Sensitive);
    }

    [Fact]
    public async Task GetEventsById_Returns404_WhenEventDoesNotExist()
    {
        var getResponse = await GetEventAsync(Guid.NewGuid());
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEventsById_OtherTenant_Returns404()
    {
        var postResponse = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-tenant-isolation"));
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var postBody = await postResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        postBody.ShouldNotBeNull();

        var getResponseForTenantB = await GetEventAsync(postBody.EventId, tenantBAuthHeaderValue);
        getResponseForTenantB.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostEvents_DuplicateSourceEventId_IsSuppressed()
    {
        var body = BuildBody(sourceEventId: "evt-dup");

        var firstResponse = await PostEventAsync(defaultSourceId, body);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        firstBody.ShouldNotBeNull();
        firstBody.AlreadyAccepted.ShouldBeFalse();

        var secondResponse = await PostEventAsync(defaultSourceId, body);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        secondBody.ShouldNotBeNull();
        secondBody.AlreadyAccepted.ShouldBeTrue();
        secondBody.EventId.ShouldBe(firstBody.EventId);

        (await fixture.GetEventCountAsync()).ShouldBe(1);
        (await fixture.GetOutboxCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task PostEvents_AttributesEventToAuthenticatedTenant()
    {
        var response = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-tenant-write"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = await response.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        body.ShouldNotBeNull();

        var writtenTenantId = await fixture.GetEventTenantIdAsync(body.EventId);
        writtenTenantId.ShouldBe(fixture.TenantAId);
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
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvents_UnknownSourceId_Returns404()
    {
        var response = await PostEventAsync(Guid.NewGuid(), BuildBody(sourceEventId: "evt-unknown-source"));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostEvents_WithInactiveConnector_Returns404()
    {
        var connectorId = await fixture.SeedSourceConnectorAsync(
            fixture.TenantAId, "inactive-source", status: "disabled");
        var sourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectorId, defaultTopicId);

        var response = await PostEventAsync(sourceId, BuildBody(sourceEventId: "evt-inactive-connector"));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostEvents_WithDestinationOnlyConnector_Returns404()
    {
        var connectorId = await fixture.SeedSourceConnectorAsync(
            fixture.TenantAId, "destination-only", direction: "destination");
        var sourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectorId, defaultTopicId);

        var response = await PostEventAsync(sourceId, BuildBody(sourceEventId: "evt-destination-only"));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostEvents_WithOtherTenantSourceId_Returns404()
    {
        var connectorId = await fixture.SeedSourceConnectorAsync(fixture.TenantBId, "other-tenant-source");
        var topicId = await fixture.SeedTopicAsync(fixture.TenantBId, "other-tenant-topic");
        var sourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantBId, connectorId, topicId);

        var response = await PostEventAsync(sourceId, BuildBody(sourceEventId: "evt-other-tenant"));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    // A Source's declarations are the only Event types intake accepts from it, however the caller
    // spells the request; the check matches the way routing does, ignoring case.
    [Fact]
    public async Task PostEvents_UndeclaredEventType_IsRefusedWithoutAnEvent()
    {
        var refused = await PostEventAsync(defaultSourceId, BuildBody("evt-undeclared", "payment.refunded"));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("payment.refunded");
        (await fixture.GetEventCountAsync()).ShouldBe(0);
        (await fixture.GetOutboxCountAsync()).ShouldBe(0);

        (await PostEventAsync(defaultSourceId, BuildBody("evt-declared", "PAYMENT.CREATED"))).StatusCode
            .ShouldBe(HttpStatusCode.Accepted);
    }

    // A webhook's Event type comes from the provider's request, so the declaration is the only thing
    // standing between a provider's new event kind and a silently unroutable Event.
    [Fact]
    public async Task PostWebhook_UndeclaredEventType_IsRefusedWithoutAnEvent()
    {
        Guid connectorId = await fixture.SeedSourceConnectorAsync(fixture.TenantAId, "webhook-source");
        Guid callbackId = await fixture.CreateWebhookSourceAsync(
            fixture.TenantAId, connectorId, defaultTopicId, "[\"github.push\"]");

        using var refused = await client.PostAsync(
            $"/webhooks/{callbackId}",
            JsonContent.Create(new { event_type = "github.ping", payload = new { zen = "Keep it logically awesome." } }));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await fixture.GetEventCountAsync()).ShouldBe(0);

        using var accepted = await client.PostAsync(
            $"/webhooks/{callbackId}",
            JsonContent.Create(new { event_type = "github.push", payload = new { @ref = "refs/heads/main" } }));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // Every entry point resolves its Source ahead of acceptance, and a broker receiver may hold a
    // resolution for as long as its reconciliation interval. Only the acceptance transaction reads
    // the Source authoritatively, so a committed disable or declaration change binds a submission
    // made from an older resolution.
    [Theory]
    [InlineData("disabled", "[\"payment.created\"]", "not enabled")]
    [InlineData("enabled", "[\"payment.updated\"]", "does not declare")]
    public async Task Acceptance_RefusesASubmissionResolvedBeforeACommittedChange(
        string status, string eventTypes, string refusal)
    {
        var acceptance = fixture.WebFactory.Services.GetRequiredService<IEventAcceptance>();
        var submission = new EventSubmission
        {
            TenantId = fixture.TenantAId,
            TopicId = defaultTopicId,
            SourceId = defaultSourceId,
            EventType = "payment.created",
            Payload = JsonSerializer.SerializeToElement(new { paymentId = "pay_stale" }),
        };
        await fixture.ChangeSourceAsync(defaultSourceId, status, eventTypes);

        var exception = await Should.ThrowAsync<EventAcceptanceException>(
            () => acceptance.AcceptAsync(submission, traceparent: null, CancellationToken.None));

        exception.Message.ShouldContain(refusal);
        (await fixture.GetEventCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Acceptance_RefusesASubmissionResolvedBeforeCommittedDeletion()
    {
        var acceptance = fixture.WebFactory.Services.GetRequiredService<IEventAcceptance>();
        var submission = new EventSubmission
        {
            TenantId = fixture.TenantAId,
            TopicId = defaultTopicId,
            SourceId = defaultSourceId,
            EventType = "payment.created",
            Payload = JsonSerializer.SerializeToElement(new { paymentId = "pay_deleted" }),
        };
        await fixture.DeleteSourceAsync(defaultSourceId);

        EventAcceptanceException exception = await Should.ThrowAsync<EventAcceptanceException>(
            () => acceptance.AcceptAsync(submission, traceparent: null, CancellationToken.None));

        exception.Message.ShouldContain("not enabled");
        (await fixture.GetEventCountAsync()).ShouldBe(0);
    }

    private static object BuildBody(string? sourceEventId, string eventType = "payment.created") => new
    {
        event_type = eventType,
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

    // The data plane answers a Tenant. A destination's response body is the Operator's downstream
    // system talking, and the accepted payload has no reason to travel back out here either, so
    // neither may appear on this response however the Admin contract grows.
    [Fact]
    public async Task GetEvent_NeverReturnsPayloadOrDeliveryBodies()
    {
        var accepted = await PostEventAsync(defaultSourceId, BuildBody(sourceEventId: "evt-no-bodies"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = await accepted.Content.ReadFromJsonAsync<IngestEventResult>(HostJson.Options);
        result.ShouldNotBeNull();

        HttpResponseMessage response = await GetEventAsync(result.EventId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (string forbidden in new[] { "payload", "metadata", "request_payload", "response_body", "response_body_truncated" })
            PropertyNames(document.RootElement).ShouldNotContain(forbidden);
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (string nested in PropertyNames(property.Value))
                        yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    foreach (string nested in PropertyNames(item))
                        yield return nested;
                break;
        }
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
