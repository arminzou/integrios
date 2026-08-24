using System.Net;
using System.Net.Http.Json;
using Integrios.Application.Events;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Integrios.Tests.Shared;

namespace Integrios.Ingress.UnitTests;

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
        (var apiKey, var tenant) = ApiKeyAuthHandlerTests.BuildValidApiKey(ApiKeyAuthHandlerTests.TestToken);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);
        fixture.EventLookup.GetEventResult = null;

        HttpResponseMessage response = await GetEventAsync(Guid.NewGuid(), $"ApiKey {ApiKeyAuthHandlerTests.TestToken}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEvent_ValidAuthAndKnownEvent_Returns200WithBody()
    {
        (var apiKey, var tenant) = ApiKeyAuthHandlerTests.BuildValidApiKey(ApiKeyAuthHandlerTests.TestToken);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

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
                    SubscriptionDeliveryId = Guid.NewGuid(),
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

        HttpResponseMessage response = await GetEventAsync(eventId, $"ApiKey {ApiKeyAuthHandlerTests.TestToken}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        EventDto? body = await response.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        Assert.NotNull(body);
        Assert.Equal(expected.EventId, body.EventId);
        Assert.Equal(expected.Status, body.Status);
        Assert.Equal(expected.AcceptedAt, body.AcceptedAt);
        Assert.Equal(expected.ProcessedAt, body.ProcessedAt);
        Assert.Equal(expected.FailedAt, body.FailedAt);
        DeliveryAttemptDto attempt = Assert.Single(body.DeliveryAttempts);
        Assert.Equal(attemptId, attempt.AttemptId);
        Assert.Equal(expected.DeliveryAttempts[0].SubscriptionDeliveryId, attempt.SubscriptionDeliveryId);
        Assert.Equal(expected.DeliveryAttempts[0].SubscriptionId, attempt.SubscriptionId);
        Assert.Equal(expected.DeliveryAttempts[0].DestinationConnectionId, attempt.DestinationConnectionId);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("succeeded", attempt.Status);
        Assert.Equal(200, attempt.ResponseStatusCode);
    }

    [Fact]
    public async Task PostEvent_MissingSourceConnectionId_Returns400()
    {
        (var apiKey, var tenant) = ApiKeyAuthHandlerTests.BuildValidApiKey(ApiKeyAuthHandlerTests.TestToken);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

        var response = await client.SendAsync(AuthorizedRequest(new
        {
            topic_name = "payments",
            event_type = "payment.created",
            payload = new { amount = 42 }
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostEvent_InvalidSourceTopicCombination_Returns422()
    {
        (var apiKey, var tenant) = ApiKeyAuthHandlerTests.BuildValidApiKey(ApiKeyAuthHandlerTests.TestToken);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);
        fixture.TopicRepository.ResolvedTopicId = null;

        var response = await client.SendAsync(AuthorizedRequest(new
        {
            source_connection_id = Guid.NewGuid(),
            topic_name = "payments",
            event_type = "payment.created",
            payload = new { amount = 42 }
        }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReplayRoute_IsNotMapped()
    {
        (var apiKey, var tenant) = ApiKeyAuthHandlerTests.BuildValidApiKey(ApiKeyAuthHandlerTests.TestToken);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

        HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post,
            $"/events/{Guid.NewGuid()}/replay")
        {
            Headers = { { "Authorization", $"ApiKey {ApiKeyAuthHandlerTests.TestToken}" } }
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage AuthorizedRequest(object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(body)
        };
        message.Headers.TryAddWithoutValidation(
            "Authorization", $"ApiKey {ApiKeyAuthHandlerTests.TestToken}");
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
