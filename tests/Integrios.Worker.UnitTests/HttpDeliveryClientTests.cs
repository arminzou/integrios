using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Worker.UnitTests;

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
        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

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

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, cts.Token);

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

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

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
            Request("https://downstream.example", ("X-Api-Key", sensitiveValue)),
            null, CancellationToken.None);

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
            Request("https://downstream.example", ("X-Api-Key", "secret")),
            null, CancellationToken.None);

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

        var result = await client.DeliverAsync(Request(url), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.StatusCode);
        Assert.Equal(DeliveryFailurePhase.RequestConstruction, result.FailurePhase);
    }

    [Fact]
    public async Task DeliverAsync_JsonBooleanAcceptsSuccessfulLogicalOutcome()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"ok":true}""")));
        var client = new HttpDeliveryClient(new HttpClient(handler));
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        var result = await client.DeliverAsync(Request("https://downstream.example"), contract, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task DeliverAsync_JsonBooleanRejectsA2xxResponseTheProviderLogicallyRejected()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"ok":false,"error":"channel_not_found"}""")));
        var client = new HttpDeliveryClient(new HttpClient(handler));
        var contract = new HttpSuccessRule
        {
            Evaluator = "json_boolean", Field = "ok", Expected = true, DiagnosticField = "error"
        };

        var result = await client.DeliverAsync(Request("https://downstream.example"), contract, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("channel_not_found", result.Error);
        Assert.Equal(DeliveryFailurePhase.Http, result.FailurePhase);
    }

    [Fact]
    public async Task DeliverAsync_JsonBooleanBodyExceedingDeclaredContentLengthBound_FailsClosed()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, new string('a', 100))));
        var client = new HttpDeliveryClient(new HttpClient(handler));
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true, MaxBodyBytes = 10 };

        var result = await client.DeliverAsync(Request("https://downstream.example"), contract, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(DeliveryFailurePhase.Http, result.FailurePhase);
    }

    [Fact]
    public async Task DeliverAsync_NonSuccessStatus_NeverInvokesTheOutcomeEvaluator()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = new HttpDeliveryClient(new HttpClient(handler));
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        var result = await client.DeliverAsync(Request("https://downstream.example"), contract, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(404, result.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DeliverAsync_RetryAfterDeltaSeconds_IsSurfacedForRetryableStatuses(HttpStatusCode statusCode)
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(statusCode);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(5), result.RetryAfter);
    }

    [Fact]
    public async Task DeliverAsync_RetryAfterBeyondTheBound_IsClampedRatherThanRejected()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return Task.FromResult(response);
        });
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(15), result.RetryAfter);
    }

    [Fact]
    public async Task DeliverAsync_RetryAfterOnNonRetryableStatus_IsIgnored()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        Assert.Null(result.RetryAfter);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static OutboundHttpMessage Request(string uri, params (string Name, string Value)[] headers) =>
        new("POST", uri, headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase), "{}");

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
