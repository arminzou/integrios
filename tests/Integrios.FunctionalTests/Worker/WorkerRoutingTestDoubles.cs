using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Secrets;
using Integrios.Domain.ValueObjects;

namespace Integrios.FunctionalTests.Worker;

public sealed class FakeDeliveryClient : IDeliveryClient
{
    public List<DeliveryCall> Calls { get; } = [];
    public bool ShouldSucceed { get; set; } = true;
    public string? ResponseBody { get; set; }
    public bool ResponseBodyTruncated { get; set; }
    public Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage request, HttpSuccessRule? successRule, CancellationToken cancellationToken = default)
    {
        Calls.Add(new DeliveryCall(request.Method, request.Uri, request.JsonBody ?? string.Empty, request.Headers));
        return Task.FromResult(ShouldSucceed
            ? new DeliveryResult(true, 200, ResponseBody: ResponseBody, ResponseBodyTruncated: ResponseBodyTruncated)
            : new DeliveryResult(false, 500, ResponseBody: ResponseBody, ResponseBodyTruncated: ResponseBodyTruncated));
    }
    public void Reset() { Calls.Clear(); ShouldSucceed = true; ResponseBody = null; ResponseBodyTruncated = false; }
}

public sealed record DeliveryCall(string Method, string Url, string Payload, IReadOnlyDictionary<string, string> Headers);

public sealed class FakeOAuthTokenEndpoint : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = [];

    public List<TokenRequest> Calls { get; } = [];

    public void EnqueueToken(string accessToken, int expiresIn = 3600) => responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = expiresIn
        }))
    });

    public void EnqueueFailure(HttpStatusCode statusCode, string body, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
        if (retryAfter.HasValue)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        responses.Enqueue(response);
    }

    public void Reset()
    {
        Calls.Clear();
        while (responses.TryDequeue(out HttpResponseMessage? response))
            response.Dispose();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls.Add(new TokenRequest(
            request.Headers.Authorization?.ToString(),
            await request.Content!.ReadAsStringAsync(cancellationToken)));
        return responses.TryDequeue(out HttpResponseMessage? response)
            ? response
            : throw new InvalidOperationException("No OAuth token response was configured for the test.");
    }
}

public sealed record TokenRequest(string? Authorization, string Body);

public sealed class MutableSecretResolver : IDestinationAuthenticationSecretResolver
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public string ProviderName => "test";
    public void Set(string reference, string value) => values[reference] = value;
    public void Reset() => values.Clear();
    public Task<string> ResolveAsync(TenantSecretScope tenant, string secretName, CancellationToken cancellationToken = default) =>
        values.TryGetValue(secretName, out string? value)
            ? Task.FromResult(value)
            : throw new InvalidOperationException($"Secret reference '{secretName}' is not configured for the test.");
}
