namespace Integrios.Application.Abstractions;

public interface IDeliveryClient
{
    Task<DeliveryResult> DeliverAsync(
        string url,
        string payloadJson,
        Action<HttpRequestMessage>? decorate = null,
        CancellationToken cancellationToken = default);
}

public record DeliveryResult(bool Succeeded, int StatusCode, string? Error = null, bool IsTimeout = false);
