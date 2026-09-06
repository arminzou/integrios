using Integrios.Application.Delivery;
using Integrios.Application.Secrets;

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
