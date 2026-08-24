using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Delivery;

public interface IDeliveryClient
{
    Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage request,
        HttpSuccessRule? successRule,
        CancellationToken cancellationToken);
}

public record DeliveryResult(
    bool Succeeded,
    int StatusCode,
    string? Error = null,
    bool IsTimeout = false,
    DeliveryFailurePhase? FailurePhase = null,
    TimeSpan? RetryAfter = null);
