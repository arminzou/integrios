namespace Integrios.Core.Contracts;

public sealed record DeliveryAttemptSummary
{
    public required Guid RouteId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public required int AttemptNumber { get; init; }
    public required string Status { get; init; }
    public int? ResponseStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
