using System.Net;
using System.Net.Http.Json;
using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Ingestion.UnitTests;

public sealed class EventEndpointTests(ApiTestAppFixture fixture)
    : IClassFixture<ApiTestAppFixture>, IAsyncLifetime
{
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        fixture.Reset();
        client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetEvent_ValidAuthAndUnknownEvent_Returns404()
    {
        (var tenantApiKey, var tenant) = TenantApiKeyAuthHandlerTests.BuildValidTenantApiKey(TenantApiKeyAuthHandlerTests.TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);
        fixture.EventLookup.GetEventResult = null;

        HttpResponseMessage response = await GetEventAsync(Guid.NewGuid(), $"TenantApiKey {TenantApiKeyAuthHandlerTests.TestToken}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEvent_ValidAuthAndKnownEvent_Returns200WithBody()
    {
        (var tenantApiKey, var tenant) = TenantApiKeyAuthHandlerTests.BuildValidTenantApiKey(TenantApiKeyAuthHandlerTests.TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);

        Guid eventId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        EventDto expected = new()
        {
            EventId = eventId,
            Status = EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            ProcessedAt = null,
            FailedAt = null,
            DeliveryAttempts =
            [
                new DeliveryAttemptDto
                {
                    AttemptId = attemptId,
                    EventDeliveryId = Guid.NewGuid(),
                    SubscriptionId = Guid.NewGuid(),
                    DestinationConnectionId = Guid.NewGuid(),
                    AttemptNumber = 1,
                    Status = "succeeded",
                    ResponseStatusCode = 200,
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        fixture.EventLookup.GetEventResult = expected;

        HttpResponseMessage response = await GetEventAsync(eventId, $"TenantApiKey {TenantApiKeyAuthHandlerTests.TestToken}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        EventDto? body = await response.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        body.ShouldNotBeNull();
        body.EventId.ShouldBe(expected.EventId);
        body.Status.ShouldBe(expected.Status);
        body.AcceptedAt.ShouldBe(expected.AcceptedAt);
        body.ProcessedAt.ShouldBe(expected.ProcessedAt);
        body.FailedAt.ShouldBe(expected.FailedAt);
        DeliveryAttemptDto attempt = body.DeliveryAttempts.ShouldHaveSingleItem();
        attempt.AttemptId.ShouldBe(attemptId);
        attempt.EventDeliveryId.ShouldBe(expected.DeliveryAttempts[0].EventDeliveryId);
        attempt.SubscriptionId.ShouldBe(expected.DeliveryAttempts[0].SubscriptionId);
        attempt.DestinationConnectionId.ShouldBe(expected.DeliveryAttempts[0].DestinationConnectionId);
        attempt.AttemptNumber.ShouldBe(1);
        attempt.Status.ShouldBe("succeeded");
        attempt.ResponseStatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task PostEvent_MissingSourceId_Returns400()
    {
        (var tenantApiKey, var tenant) = TenantApiKeyAuthHandlerTests.BuildValidTenantApiKey(TenantApiKeyAuthHandlerTests.TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);

        var response = await client.SendAsync(AuthorizedRequest(new
        {
            event_type = "payment.created",
            payload = new { amount = 42 }
        }, sourceId: null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvent_UnknownSourceId_Returns404()
    {
        (var tenantApiKey, var tenant) = TenantApiKeyAuthHandlerTests.BuildValidTenantApiKey(TenantApiKeyAuthHandlerTests.TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);
        fixture.EventApiSourceResolver.Result = null;

        var response = await client.SendAsync(AuthorizedRequest(new
        {
            event_type = "payment.created",
            payload = new { amount = 42 }
        }, sourceId: Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostEvent_PassthroughContractMissingEventType_Returns422()
    {
        (var tenantApiKey, var tenant) = TenantApiKeyAuthHandlerTests.BuildValidTenantApiKey(TenantApiKeyAuthHandlerTests.TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);
        fixture.EventApiSourceResolver.Result = new ResolvedEventApiSource
        {
            TopicId = Guid.NewGuid(),
            SourceContractSchema = null,
            SourceMapping = null,
        };

        var response = await client.SendAsync(AuthorizedRequest(new
        {
            payload = new { amount = 42 }
        }, sourceId: Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ReplayRoute_IsNotMapped()
    {
        (var tenantApiKey, var tenant) = TenantApiKeyAuthHandlerTests.BuildValidTenantApiKey(TenantApiKeyAuthHandlerTests.TestToken);
        fixture.TenantApiKeyRepository.Result = (tenantApiKey, tenant);

        HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post,
            $"/events/{Guid.NewGuid()}/replay")
        {
            Headers = { { "Authorization", $"TenantApiKey {TenantApiKeyAuthHandlerTests.TestToken}" } }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static HttpRequestMessage AuthorizedRequest(object body, Guid? sourceId)
    {
        string uri = sourceId is { } id ? $"/events?source_id={id}" : "/events";
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body)
        };
        message.Headers.TryAddWithoutValidation(
            "Authorization", $"TenantApiKey {TenantApiKeyAuthHandlerTests.TestToken}");
        return message;
    }

    private Task<HttpResponseMessage> GetEventAsync(Guid eventId, string? authHeader)
    {
        HttpRequestMessage message = new(HttpMethod.Get, $"/events/{eventId}");
        if (authHeader is not null)
            message.Headers.TryAddWithoutValidation("Authorization", authHeader);

        return client.SendAsync(message);
    }
}
