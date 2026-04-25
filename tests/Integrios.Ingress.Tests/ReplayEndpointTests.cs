using System.Net;
using Integrios.Ingress.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Ingress.Tests;

public sealed class ReplayEndpointTests(ApiTestAppFixture fixture)
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
    public async Task Replay_NoAuth_Returns401()
    {
        var response = await ReplayAsync(Guid.NewGuid(), authHeader: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Replay_ValidAuth_ReplayableEvent_Returns202WithLocation()
    {
        var (apiKey, tenant) = ApiKeyEndpointFilterTests.BuildValidApiKeyPublic("secret");
        fixture.ApiKeyRepository.Result = (apiKey, tenant);
        fixture.EventRepository.ReplayResult = true;

        var eventId = Guid.NewGuid();
        var response = await ReplayAsync(eventId, $"ApiKey {apiKey.PublicKey}:secret");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/events/{eventId}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Replay_ValidAuth_NonReplayableEvent_Returns404()
    {
        var (apiKey, tenant) = ApiKeyEndpointFilterTests.BuildValidApiKeyPublic("secret");
        fixture.ApiKeyRepository.Result = (apiKey, tenant);
        fixture.EventRepository.ReplayResult = false;

        var response = await ReplayAsync(Guid.NewGuid(), $"ApiKey {apiKey.PublicKey}:secret");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private Task<HttpResponseMessage> ReplayAsync(Guid eventId, string? authHeader)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/events/{eventId}/replay");
        if (authHeader is not null)
            message.Headers.TryAddWithoutValidation("Authorization", authHeader);
        return client.SendAsync(message);
    }
}
