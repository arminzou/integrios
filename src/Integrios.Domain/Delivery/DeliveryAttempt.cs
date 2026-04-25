namespace Integrios.Domain.Delivery;

public sealed record DeliveryAttempt
{
    public required Guid Id { get; init; }
    public required Guid EventId { get; init; }
    public required Guid RouteId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public required int AttemptNumber { get; init; }
    public required DeliveryAttemptStatus Status { get; init; }
    public string? RequestPayload { get; init; }
    public int? ResponseStatusCode { get; init; }
    public string? ResponseBody { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
}
