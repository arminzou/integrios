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
    TimeSpan? RetryAfter = null,
    // What the destination actually returned, bounded at capture. An Operator diagnosing a failed
    // Delivery is asking this question, and the status code alone rarely answers it.
    string? ResponseBody = null,
    bool ResponseBodyTruncated = false);
