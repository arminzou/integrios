using System.Net;
using Integrios.Infrastructure.Http;

namespace Integrios.Worker.Tests;

public sealed class HttpDeliveryClientTests
{
    [Fact]
    public async Task DeliverAsync_WhenClientTimeoutFires_ReturnsTimeout()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = new HttpDeliveryClient(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) });
        var result = await client.DeliverAsync("https://downstream.example", "{}");

        Assert.False(result.Succeeded);
        Assert.True(result.IsTimeout);
    }

    [Fact]
    public async Task DeliverAsync_WhenCallerCancels_IsNotTimeout()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync("https://downstream.example", "{}", cancellationToken: cts.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.IsTimeout);
    }

    [Fact]
    public async Task DeliverAsync_AppliesRequestDecoratorBeforeSend()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(
            "https://downstream.example",
            "{}",
            request => request.Headers.TryAddWithoutValidation("X-Api-Key", "secret"));

        Assert.True(result.Succeeded);
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values));
        Assert.Equal(["secret"], values);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
