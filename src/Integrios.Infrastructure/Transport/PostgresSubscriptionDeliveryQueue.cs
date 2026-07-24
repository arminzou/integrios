using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Delivery;
using Integrios.Domain.Delivery;
using Integrios.Infrastructure.Data;
using Npgsql;

namespace Integrios.Infrastructure.Transport;

public sealed class PostgresSubscriptionDeliveryQueue(
    IDbConnectionFactory connectionFactory,
    DeliveryExecutionOptions options,
    DeliveryOutcomePolicy outcomePolicy) : ISubscriptionDeliveryQueue
{
    private static readonly TimeSpan[] FinalizationRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200)
    ];

    public async Task<SubscriptionDeliveryWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<DeliveryWorkRow>(
            new CommandDefinition(
                """
                SELECT
                    sd.id AS Id,
                    sd.event_id AS EventId,
                    sd.subscription_id AS SubscriptionId,
                    sd.destination_connection_id AS DestinationConnectionId,
                    sd.status AS Status,
                    sd.lifetime_attempt_count AS LifetimeAttemptCount,
                    sd.retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    sd.active_attempt_id AS ActiveAttemptId,
                    sd.destination_url AS DestinationUrl,
                    sd.integration_key AS IntegrationKey,
                    sd.destination_auth::text AS DestinationAuthJson,
                    sd.transform_config_snapshot AS TransformConfigSnapshot,
                    sd.traceparent AS Traceparent,
                    e.tenant_id AS TenantId,
                    e.payload::text AS PayloadJson,
                    e.event_type AS EventType,
                    e.accepted_at AS AcceptedAt,
                    t.name AS TopicName,
                    now() AS DatabaseNow
                FROM subscription_deliveries sd
                JOIN events e ON e.id = sd.event_id
                LEFT JOIN topics t ON t.id = e.topic_id
                WHERE (
                    sd.status = 'in_flight'
                    AND sd.lease_expires_at <= now()
                ) OR (
                    sd.status = 'pending'
                    AND (sd.deliver_after IS NULL OR sd.deliver_after <= now())
                )
                ORDER BY
                    CASE WHEN sd.status = 'in_flight' THEN 0 ELSE 1 END,
                    sd.lease_expires_at NULLS LAST,
                    sd.deliver_after NULLS FIRST,
                    sd.created_at,
                    sd.id
                LIMIT 1
                FOR UPDATE OF sd SKIP LOCKED;
                """,
                transaction: transaction,
                cancellationToken: cancellationToken));

            if (row is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            if (row.Status == "in_flight")
            {
                int finalized = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE delivery_attempts
                    SET status = 'indeterminate',
                        completed_at = @DatabaseNow,
                        error_message = 'Lease expired before the owning worker finalized this attempt.'
                    WHERE id = @ActiveAttemptId
                      AND subscription_delivery_id = @Id
                      AND status = 'in_progress';
                    """,
                    row,
                    transaction,
                    cancellationToken: cancellationToken));

                if (finalized != 1)
                    throw new InvalidOperationException($"Active attempt {row.ActiveAttemptId} for delivery {row.Id} could not be made indeterminate.");

                DeliveryOutcomeDecision recoveryDecision = outcomePolicy.Decide(
                    DeliveryOutcomeKind.Indeterminate,
                    row.RetryCycleAttemptCount,
                    row.DatabaseNow);

                if (recoveryDecision.Disposition == SubscriptionDeliveryDisposition.DeadLettered)
                {
                    await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE subscription_deliveries
                        SET status = 'dead_lettered',
                            active_attempt_id = NULL,
                            lease_expires_at = NULL,
                            deliver_after = NULL,
                            failed_at = @DatabaseNow,
                            updated_at = @DatabaseNow
                        WHERE id = @Id;
                        """,
                        row,
                        transaction,
                        cancellationToken: cancellationToken));

                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }
            }

            Guid attemptId = Guid.NewGuid();
            int attemptNumber = row.LifetimeAttemptCount + 1;
            int retryCycleAttemptCount = row.RetryCycleAttemptCount + 1;

            await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO delivery_attempts (
                    id,
                    subscription_delivery_id,
                    attempt_number,
                    status,
                    started_at)
                VALUES (
                    @AttemptId,
                    @DeliveryId,
                    @AttemptNumber,
                    'in_progress',
                    @DatabaseNow);

                UPDATE subscription_deliveries
                SET status = 'in_flight',
                    lifetime_attempt_count = @AttemptNumber,
                    retry_cycle_attempt_count = @RetryCycleAttemptCount,
                    active_attempt_id = @AttemptId,
                    lease_expires_at = @DatabaseNow + @LeaseDuration,
                    deliver_after = NULL,
                    updated_at = @DatabaseNow
                WHERE id = @DeliveryId;
                """,
                new
                {
                    AttemptId = attemptId,
                    DeliveryId = row.Id,
                    AttemptNumber = attemptNumber,
                    RetryCycleAttemptCount = retryCycleAttemptCount,
                    row.DatabaseNow,
                    options.LeaseDuration
                },
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            return new SubscriptionDeliveryWorkItem(
                row.Id,
                attemptId,
                attemptNumber,
                row.EventId,
                row.SubscriptionId,
                row.DestinationConnectionId,
                row.TenantId,
                row.DestinationUrl ?? string.Empty,
                row.PayloadJson ?? string.Empty,
                row.EventType ?? string.Empty,
                row.TopicName,
                row.AcceptedAt,
                row.TransformConfigSnapshot,
                row.IntegrationKey ?? string.Empty,
                row.DestinationAuthJson,
                row.Traceparent);
        }
    }

    public async Task<DeliveryFinalizationResult> FinalizeAsync(
        DeliveryAttemptCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ValidateCompletion(completion);

        for (int retry = 0; ; retry++)
        {
            try
            {
                return await FinalizeOnceAsync(completion, cancellationToken);
            }
            catch (NpgsqlException exception) when (
                exception.IsTransient
                && retry < FinalizationRetryDelays.Length
                && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(FinalizationRetryDelays[retry], cancellationToken);
            }
        }
    }

    public async Task<bool> ReplayDeadLetteredAsync(
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        int resetCount = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscription_deliveries sd
                SET status = 'pending',
                    retry_cycle_attempt_count = 0,
                    deliver_after = NULL,
                    active_attempt_id = NULL,
                    lease_expires_at = NULL,
                    failed_at = NULL,
                    updated_at = now()
                FROM events e
                WHERE sd.event_id = e.id
                  AND e.tenant_id = @TenantId
                  AND e.id = @EventId
                  AND sd.status = 'dead_lettered';
                """,
                new { TenantId = tenantId, EventId = eventId },
                cancellationToken: cancellationToken));

        return resetCount > 0;
    }

    private async Task<DeliveryFinalizationResult> FinalizeOnceAsync(
        DeliveryAttemptCompletion completion,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var owner = await connection.QuerySingleOrDefaultAsync<DeliveryOwnerRow>(
            new CommandDefinition(
                """
                SELECT
                    id AS Id,
                    status AS Status,
                    active_attempt_id AS ActiveAttemptId,
                    retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    now() AS DatabaseNow
                FROM subscription_deliveries
                WHERE id = @DeliveryId
                FOR UPDATE;
                """,
                completion,
                transaction,
                cancellationToken: cancellationToken));

        if (owner is null
            || owner.Status != "in_flight"
            || owner.ActiveAttemptId != completion.AttemptId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(DeliveryFinalizationStatus.OwnershipLost);
        }

        DeliveryOutcomeDecision decision = outcomePolicy.Decide(
            completion.Succeeded ? DeliveryOutcomeKind.Succeeded : DeliveryOutcomeKind.Failed,
            owner.RetryCycleAttemptCount,
            owner.DatabaseNow);

        int finalized = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE delivery_attempts
                SET status = @AttemptStatus,
                    failure_phase = @FailurePhase,
                    request_payload = CAST(@RequestPayloadJson AS jsonb),
                    response_status_code = @ResponseStatusCode,
                    response_body = @ResponseBody,
                    error_message = @ErrorMessage,
                    completed_at = @DatabaseNow
                WHERE id = @AttemptId
                  AND subscription_delivery_id = @DeliveryId
                  AND status = 'in_progress';
                """,
                new
                {
                    completion.AttemptId,
                    completion.DeliveryId,
                    AttemptStatus = completion.Succeeded ? "succeeded" : "failed",
                    FailurePhase = MapFailurePhase(completion.FailurePhase),
                    completion.RequestPayloadJson,
                    completion.ResponseStatusCode,
                    completion.ResponseBody,
                    completion.ErrorMessage,
                    owner.DatabaseNow
                },
                transaction,
                cancellationToken: cancellationToken));

        if (finalized != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(DeliveryFinalizationStatus.OwnershipLost);
        }

        string deliveryStatus = decision.Disposition switch
        {
            SubscriptionDeliveryDisposition.Succeeded => "succeeded",
            SubscriptionDeliveryDisposition.RetryScheduled => "pending",
            SubscriptionDeliveryDisposition.DeadLettered => "dead_lettered",
            _ => throw new ArgumentOutOfRangeException()
        };

        int advanced = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscription_deliveries
                SET status = @DeliveryStatus,
                    active_attempt_id = NULL,
                    lease_expires_at = NULL,
                    deliver_after = @DeliverAfter,
                    processed_at = CASE WHEN @DeliveryStatus = 'succeeded' THEN @DatabaseNow ELSE processed_at END,
                    failed_at = CASE WHEN @DeliveryStatus = 'dead_lettered' THEN @DatabaseNow ELSE NULL END,
                    updated_at = @DatabaseNow
                WHERE id = @DeliveryId
                  AND active_attempt_id = @AttemptId;
                """,
                new
                {
                    completion.DeliveryId,
                    completion.AttemptId,
                    DeliveryStatus = deliveryStatus,
                    decision.DeliverAfter,
                    owner.DatabaseNow
                },
                transaction,
                cancellationToken: cancellationToken));

        if (advanced != 1)
            throw new InvalidOperationException($"Delivery {completion.DeliveryId} lost ownership during finalization.");

        await transaction.CommitAsync(cancellationToken);
        return new(DeliveryFinalizationStatus.Applied, decision.Disposition);
    }

    private static void ValidateCompletion(DeliveryAttemptCompletion completion)
    {
        if (completion.Succeeded && completion.FailurePhase is not null)
            throw new ArgumentException("A succeeded delivery attempt cannot have a failure phase.", nameof(completion));
        if (!completion.Succeeded && completion.FailurePhase is null)
            throw new ArgumentException("A failed delivery attempt requires a failure phase.", nameof(completion));
    }

    private static string? MapFailurePhase(DeliveryFailurePhase? phase) => phase switch
    {
        null => null,
        DeliveryFailurePhase.Transform => "transform",
        DeliveryFailurePhase.SecretResolution => "secret_resolution",
        DeliveryFailurePhase.RequestConstruction => "request_construction",
        DeliveryFailurePhase.Http => "http",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    private sealed record DeliveryOwnerRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = string.Empty;
        public Guid? ActiveAttemptId { get; init; }
        public int RetryCycleAttemptCount { get; init; }
        public DateTimeOffset DatabaseNow { get; init; }
    }

    private sealed record DeliveryWorkRow
    {
        public Guid Id { get; init; }
        public Guid EventId { get; init; }
        public Guid SubscriptionId { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public Guid TenantId { get; init; }
        public string Status { get; init; } = string.Empty;
        public int LifetimeAttemptCount { get; init; }
        public int RetryCycleAttemptCount { get; init; }
        public Guid? ActiveAttemptId { get; init; }
        public string? DestinationUrl { get; init; }
        public string? PayloadJson { get; init; }
        public string? EventType { get; init; }
        public string? TopicName { get; init; }
        public DateTimeOffset AcceptedAt { get; init; }
        public string? TransformConfigSnapshot { get; init; }
        public string? IntegrationKey { get; init; }
        public string? DestinationAuthJson { get; init; }
        public string? Traceparent { get; init; }
        public DateTimeOffset DatabaseNow { get; init; }
    }
}
