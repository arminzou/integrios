using System.Net;
using Integrios.Domain.Delivery;
using Integrios.Infrastructure.Delivery;

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
        Assert.Equal("Request was canceled.", result.Error);
    }

    [Fact]
    public async Task DeliverAsync_HttpRequestFailure_PreservesTransportDiagnostic()
    {
        const string diagnostic = "Connection refused by downstream host.";
        var handler = new StubHandler((_, _) => throw new HttpRequestException(diagnostic));
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync("https://downstream.example", "{}");

        Assert.False(result.Succeeded);
        Assert.Equal(DeliveryFailurePhase.Http, result.FailurePhase);
        Assert.Equal(diagnostic, result.Error);
    }

    [Fact]
    public async Task DeliverAsync_UnexpectedSendFailure_ReplacesExceptionMessage()
    {
        const string sensitiveValue = "sensitive-header-value";
        var handler = new StubHandler((request, _) =>
        {
            Assert.True(request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values));
            throw new FormatException($"Unexpected handler failure involving '{values.Single()}'.");
        });
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(
            "https://downstream.example",
            "{}",
            request => request.Headers.TryAddWithoutValidation("X-Api-Key", sensitiveValue));

        Assert.False(result.Succeeded);
        Assert.Equal(DeliveryFailurePhase.Http, result.FailurePhase);
        Assert.Equal("Outbound HTTP request failed.", result.Error);
        Assert.DoesNotContain(sensitiveValue, result.Error);
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

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://example.test/sink")]
    [InlineData("not a url")]
    public async Task DeliverAsync_InvalidDestination_ReturnsRequestConstructionFailure(string url)
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(url, "{}");

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.StatusCode);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, result.FailurePhase);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
