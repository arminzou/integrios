using Integrios.Domain.Delivery;

namespace Integrios.Application.Delivery;

public interface IDeliveryClient
{
    Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage request,
        CancellationToken cancellationToken);
}

public record DeliveryResult(
    bool Succeeded,
    int StatusCode,
    string? Error = null,
    bool IsTimeout = false,
    DeliveryFailurePhase? FailurePhase = null);
