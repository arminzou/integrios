using System.Net;
using System.Net.Http.Json;
using Integrios.Application.Events;
using Integrios.Domain.Events;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Ingress.Tests;

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
        fixture.EventRepository.GetEventResult = null;

        HttpResponseMessage response = await GetEventAsync(Guid.NewGuid(), $"ApiKey {ApiKeyAuthHandlerTests.TestToken}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEvent_ValidAuthAndKnownEvent_Returns200WithBody()
    {
        (var apiKey, var tenant) = ApiKeyAuthHandlerTests.BuildValidApiKey(ApiKeyAuthHandlerTests.TestToken);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

        Guid eventId = Guid.NewGuid();
        GetEventResponse expected = new()
        {
            EventId = eventId,
            Status = EventStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
            ProcessedAt = null,
            FailedAt = null
        };
        fixture.EventRepository.GetEventResult = expected;

        HttpResponseMessage response = await GetEventAsync(eventId, $"ApiKey {ApiKeyAuthHandlerTests.TestToken}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        GetEventResponse? body = await response.Content.ReadFromJsonAsync<GetEventResponse>();
        Assert.NotNull(body);
        Assert.Equal(expected.EventId, body.EventId);
        Assert.Equal(expected.Status, body.Status);
        Assert.Equal(expected.AcceptedAt, body.AcceptedAt);
        Assert.Equal(expected.ProcessedAt, body.ProcessedAt);
        Assert.Equal(expected.FailedAt, body.FailedAt);
    }

    [Fact]
    public async Task PostEvent_MissingSourceConnectionId_Returns400()
    {
        (var apiKey, var tenant) = ApiKeyAuthHandlerTests.BuildValidApiKey(ApiKeyAuthHandlerTests.TestToken);
        fixture.ApiKeyRepository.Result = (apiKey, tenant);

        var response = await client.SendAsync(AuthorizedRequest(new
        {
            topicName = "payments",
            eventType = "payment.created",
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
            sourceConnectionId = Guid.NewGuid(),
            topicName = "payments",
            eventType = "payment.created",
            payload = new { amount = 42 }
        }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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
