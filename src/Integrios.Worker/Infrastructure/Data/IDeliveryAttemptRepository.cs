namespace Integrios.Worker.Infrastructure.Data;

public interface IDeliveryAttemptRepository
{
    Task<int> GetAttemptCountAsync(Guid eventId, Guid routeId, CancellationToken cancellationToken = default);

    Task RecordAsync(
        Guid eventId,
        Guid routeId,
        Guid destinationConnectionId,
        int attemptNumber,
        string status,
        string requestPayloadJson,
        int? responseStatusCode,
        string? responseBody,
        string? errorMessage,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken = default);
}
