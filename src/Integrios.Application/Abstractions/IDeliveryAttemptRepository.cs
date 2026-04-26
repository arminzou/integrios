namespace Integrios.Application.Abstractions;

public interface IDeliveryAttemptRepository
{
    Task<int> GetAttemptCountAsync(Guid eventId, Guid subscriptionId, CancellationToken cancellationToken = default);
    Task RecordAsync(Guid eventId, Guid subscriptionId, Guid destinationConnectionId, int attemptNumber, string status, string requestPayloadJson, int? responseStatusCode, string? responseBody, string? errorMessage, DateTimeOffset startedAt, DateTimeOffset? completedAt, CancellationToken cancellationToken = default);
}
