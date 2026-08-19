using System.Text.Json;

namespace Integrios.Domain.Delivery;

public sealed record DeliveryAttempt
{
    public required Guid Id { get; init; }
    public required Guid SubscriptionDeliveryId { get; init; }
    public required int AttemptNumber { get; init; }
    public required DeliveryAttemptStatus Status { get; init; }
    public JsonElement? RequestPayload { get; init; }
    public int? ResponseStatusCode { get; init; }
    public string? ResponseBody { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DeliveryFailurePhase? FailurePhase { get; init; }
}
