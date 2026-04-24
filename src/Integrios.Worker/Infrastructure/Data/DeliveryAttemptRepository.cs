using Dapper;

namespace Integrios.Worker.Infrastructure.Data;

public sealed class DeliveryAttemptRepository(IDbConnectionFactory connectionFactory) : IDeliveryAttemptRepository
{
    public async Task<int> GetAttemptCountAsync(
        Guid eventId,
        Guid routeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM delivery_attempts WHERE event_id = @EventId AND route_id = @RouteId",
                new { EventId = eventId, RouteId = routeId },
                cancellationToken: cancellationToken));
    }

    public async Task RecordAsync(
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
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO delivery_attempts (
                    event_id,
                    route_id,
                    destination_connection_id,
                    attempt_number,
                    status,
                    request_payload,
                    response_status_code,
                    response_body,
                    error_message,
                    started_at,
                    completed_at
                )
                VALUES (
                    @EventId,
                    @RouteId,
                    @DestinationConnectionId,
                    @AttemptNumber,
                    @Status,
                    CAST(@RequestPayloadJson AS jsonb),
                    @ResponseStatusCode,
                    @ResponseBody,
                    @ErrorMessage,
                    @StartedAt,
                    @CompletedAt
                )
                """,
                new
                {
                    EventId = eventId,
                    RouteId = routeId,
                    DestinationConnectionId = destinationConnectionId,
                    AttemptNumber = attemptNumber,
                    Status = status,
                    RequestPayloadJson = requestPayloadJson,
                    ResponseStatusCode = responseStatusCode,
                    ResponseBody = responseBody,
                    ErrorMessage = errorMessage,
                    StartedAt = startedAt,
                    CompletedAt = completedAt
                },
                cancellationToken: cancellationToken));
    }
}
