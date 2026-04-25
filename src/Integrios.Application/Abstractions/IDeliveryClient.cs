namespace Integrios.Application.Abstractions;

public interface IDeliveryClient
{
    Task<DeliveryResult> DeliverAsync(string url, string payloadJson, CancellationToken cancellationToken = default);
}

public record DeliveryResult(bool Succeeded, int StatusCode, string? Error = null);
