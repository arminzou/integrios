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
        var client = new HttpDeliveryClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await client.DeliverAsync("https://downstream.example", "{}", cts.Token);

        Assert.False(result.Succeeded);
        Assert.False(result.IsTimeout);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
