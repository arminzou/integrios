using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Infrastructure.UnitTests;

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

        result.Succeeded.ShouldBeFalse();
        result.IsTimeout.ShouldBeTrue();
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

        result.Succeeded.ShouldBeFalse();
        result.IsTimeout.ShouldBeFalse();
        result.Error.ShouldBe("Request was canceled.");
    }

    [Fact]
    public async Task DeliverAsync_HttpRequestFailure_PreservesTransportDiagnostic()
    {
        const string diagnostic = "Connection refused by downstream host.";
        var handler = new StubHandler((_, _) => throw new HttpRequestException(diagnostic));
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.FailurePhase.ShouldBe(DeliveryFailurePhase.Http);
        result.Error.ShouldBe(diagnostic);
    }

    [Fact]
    public async Task DeliverAsync_UnexpectedSendFailure_ReplacesExceptionMessage()
    {
        const string sensitiveValue = "sensitive-header-value";
        var handler = new StubHandler((request, _) =>
        {
            request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values).ShouldBeTrue();
            throw new FormatException($"Unexpected handler failure involving '{values.Single()}'.");
        });
        var client = new HttpDeliveryClient(new HttpClient(handler));

        var result = await client.DeliverAsync(
            Request("https://downstream.example", ("X-Api-Key", sensitiveValue)),
            null, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.FailurePhase.ShouldBe(DeliveryFailurePhase.Http);
        result.Error.ShouldBe("Outbound HTTP request failed.");
        result.Error!.ShouldNotContain(sensitiveValue, Case.Sensitive);
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

        result.Succeeded.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values).ShouldBeTrue();
        values.ShouldBe(["secret"]);
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

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBe(0);
        result.FailurePhase.ShouldBe(DeliveryFailurePhase.RequestConstruction);
    }

    [Fact]
    public async Task DeliverAsync_JsonBooleanAcceptsSuccessfulLogicalOutcome()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"ok":true}""")));
        var client = new HttpDeliveryClient(new HttpClient(handler));
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        var result = await client.DeliverAsync(Request("https://downstream.example"), contract, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);
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

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBe(200);
        result.Error.ShouldBe("channel_not_found");
        result.FailurePhase.ShouldBe(DeliveryFailurePhase.Http);
    }

    [Fact]
    public async Task DeliverAsync_JsonBooleanBodyExceedingDeclaredContentLengthBound_FailsClosed()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, new string('a', 100))));
        var client = new HttpDeliveryClient(new HttpClient(handler));
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true, MaxBodyBytes = 10 };

        var result = await client.DeliverAsync(Request("https://downstream.example"), contract, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBe(200);
        result.FailurePhase.ShouldBe(DeliveryFailurePhase.Http);
    }

    [Fact]
    public async Task DeliverAsync_NonSuccessStatus_NeverInvokesTheOutcomeEvaluator()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = new HttpDeliveryClient(new HttpClient(handler));
        var contract = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true };

        var result = await client.DeliverAsync(Request("https://downstream.example"), contract, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBe(404);
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

        result.RetryAfter.ShouldBe(TimeSpan.FromSeconds(5));
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

        result.RetryAfter.ShouldBe(TimeSpan.FromMinutes(15));
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

        result.RetryAfter.ShouldBeNull();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DeliverAsync_CapturesTheResponseBody_OnSuccessAndFailureAlike(HttpStatusCode status)
    {
        const string body = """{"error":"upstream unavailable"}""";
        var client = new HttpDeliveryClient(new HttpClient(Responds(status, body)));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        result.ResponseBody.ShouldBe(body);
        result.ResponseBodyTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task DeliverAsync_BodyOverTheCeiling_IsStoredTruncatedAndFlagged()
    {
        string body = new('a', HttpDeliveryClient.CaptureMaxBytes + 1);
        var client = new HttpDeliveryClient(new HttpClient(Responds(HttpStatusCode.OK, body)));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        result.ResponseBodyTruncated.ShouldBeTrue();
        result.ResponseBody!.Length.ShouldBe(HttpDeliveryClient.CaptureMaxBytes);
    }

    [Fact]
    public async Task DeliverAsync_BodyExactlyAtTheCeiling_IsNotReportedAsTruncated()
    {
        string body = new('a', HttpDeliveryClient.CaptureMaxBytes);
        var client = new HttpDeliveryClient(new HttpClient(Responds(HttpStatusCode.OK, body)));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        result.ResponseBodyTruncated.ShouldBeFalse();
        result.ResponseBody!.Length.ShouldBe(HttpDeliveryClient.CaptureMaxBytes);
    }

    // A character split by the ceiling is dropped rather than kept as a replacement character the
    // destination never sent.
    [Fact]
    public async Task DeliverAsync_MultiByteCharacterSplitByTheCeiling_IsDroppedNotCorrupted()
    {
        // One byte short of the ceiling, so the three-byte character that follows straddles it.
        string body = new string('a', HttpDeliveryClient.CaptureMaxBytes - 1) + "€uro";
        var client = new HttpDeliveryClient(new HttpClient(Responds(HttpStatusCode.OK, body)));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        result.ResponseBodyTruncated.ShouldBeTrue();
        result.ResponseBody.ShouldBe(new string('a', HttpDeliveryClient.CaptureMaxBytes - 1));
        result.ResponseBody!.ShouldNotContain('�');
    }

    [Fact]
    public async Task DeliverAsync_EmptyResponseBody_IsStoredAsNullRatherThanBlank()
    {
        var client = new HttpDeliveryClient(new HttpClient(Responds(HttpStatusCode.NoContent, "")));

        var result = await client.DeliverAsync(Request("https://downstream.example"), null, CancellationToken.None);

        result.ResponseBody.ShouldBeNull();
        result.ResponseBodyTruncated.ShouldBeFalse();
    }

    // Capture reads the same bytes an outcome rule reads. A rule that still accepts the body proves
    // capture did not consume the stream out from under it.
    [Fact]
    public async Task DeliverAsync_WithAnOutcomeRule_EvaluatesAndCapturesTheSameBody()
    {
        const string body = """{"ok":true}""";
        var rule = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true, MaxBodyBytes = 64 };
        var client = new HttpDeliveryClient(new HttpClient(Responds(HttpStatusCode.OK, body)));

        var result = await client.DeliverAsync(Request("https://downstream.example"), rule, CancellationToken.None);

        result.Succeeded.ShouldBeTrue();
        result.ResponseBody.ShouldBe(body);
    }

    // The rule's own bound still governs the outcome, and is unaffected by the larger capture read.
    [Fact]
    public async Task DeliverAsync_BodyOverTheOutcomeRuleBound_StillFailsButIsCaptured()
    {
        string body = $$"""{"ok":true,"pad":"{{new string('a', 200)}}"}""";
        var rule = new HttpSuccessRule { Evaluator = "json_boolean", Field = "ok", Expected = true, MaxBodyBytes = 64 };
        var client = new HttpDeliveryClient(new HttpClient(Responds(HttpStatusCode.OK, body)));

        var result = await client.DeliverAsync(Request("https://downstream.example"), rule, CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe("Response body exceeded the configured outcome-evaluation bound.");
        result.ResponseBody.ShouldBe(body);
        result.ResponseBodyTruncated.ShouldBeFalse();
    }

    private static StubHandler Responds(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

    private static OutboundHttpMessage Request(string uri, params (string Name, string Value)[] headers) =>
        new("POST", uri, headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase), "{}");

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
