using Dapper;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Integrios.Infrastructure.Delivery;

internal sealed class EventDeliveryQueue(
    IDbConnectionFactory connectionFactory,
    DeliveryExecutionOptions options,
    DeliveryOutcomePolicy outcomePolicy) : IEventDeliveryQueue
{
    private static readonly TimeSpan[] FinalizationRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200)
    ];

    internal async Task<EventDeliveryWorkItem?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            EventDeliveryClaimResult? result = await ClaimNextWithRecoveryAsync(cancellationToken);
            switch (result)
            {
                case null:
                    return null;
                case ClaimedEventDelivery claimed:
                    return claimed.WorkItem;
                case RecoveredEventDeliveryDeadLetter:
                    continue;
                default:
                    throw new InvalidOperationException($"Unknown delivery claim result '{result.GetType().Name}'.");
            }
        }
    }

    public async Task<EventDeliveryClaimResult?> ClaimNextWithRecoveryAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string databaseNow = sqlServer ? "SYSUTCDATETIME()" : "now()";
        string claimable = EventDeliveryClaimability.Predicate(connectionFactory.Provider, "sd", databaseNow);
        string claimSql = sqlServer
            ? $"""
                SELECT TOP (1)
                    sd.id AS Id, sd.event_id AS EventId, sd.subscription_id AS SubscriptionId,
                    sd.destination_connection_id AS DestinationConnectionId, sd.status AS Status,
                    sd.lifetime_attempt_count AS LifetimeAttemptCount,
                    sd.retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    sd.active_attempt_id AS ActiveAttemptId, sd.connector_key AS ConnectorKey,
                    sd.http_execution_snapshot AS HttpExecutionSnapshotJson,
                    sd.mapping_config_snapshot AS MappingConfigSnapshot, sd.traceparent AS Traceparent,
                    e.tenant_id AS TenantId, tenant.slug AS TenantSlug, e.payload AS PayloadJson,
                    e.event_type AS EventType, e.accepted_at AS AcceptedAt, t.name AS TopicName,
                    SYSUTCDATETIME() AS DatabaseNow
                FROM event_deliveries sd WITH (UPDLOCK, ROWLOCK, READPAST, READCOMMITTEDLOCK)
                JOIN events e ON e.id=sd.event_id
                JOIN tenants tenant ON tenant.id=e.tenant_id
                LEFT JOIN topics t ON t.id=e.topic_id
                WHERE {claimable}
                ORDER BY CASE WHEN sd.status=N'in_flight' THEN 0 ELSE 1 END,
                    CASE WHEN sd.lease_expires_at IS NULL THEN 1 ELSE 0 END, sd.lease_expires_at,
                    CASE WHEN sd.deliver_after IS NULL THEN 0 ELSE 1 END, sd.deliver_after,
                    sd.created_at, sd.id
                """
            : $"""
                SELECT
                    sd.id AS Id,
                    sd.event_id AS EventId,
                    sd.subscription_id AS SubscriptionId,
                    sd.destination_connection_id AS DestinationConnectionId,
                    sd.status AS Status,
                    sd.lifetime_attempt_count AS LifetimeAttemptCount,
                    sd.retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    sd.active_attempt_id AS ActiveAttemptId,
                    sd.connector_key AS ConnectorKey,
                    sd.http_execution_snapshot::text AS HttpExecutionSnapshotJson,
                    sd.mapping_config_snapshot AS MappingConfigSnapshot,
                    sd.traceparent AS Traceparent,
                    e.tenant_id AS TenantId,
                    tenant.slug AS TenantSlug,
                    e.payload::text AS PayloadJson,
                    e.event_type AS EventType,
                    e.accepted_at AS AcceptedAt,
                    t.name AS TopicName,
                    now() AS DatabaseNow
                FROM event_deliveries sd
                JOIN events e ON e.id = sd.event_id
                JOIN tenants tenant ON tenant.id = e.tenant_id
                LEFT JOIN topics t ON t.id = e.topic_id
                WHERE {claimable}
                ORDER BY CASE WHEN sd.status = 'in_flight' THEN 0 ELSE 1 END,
                    sd.lease_expires_at NULLS LAST, sd.deliver_after NULLS FIRST, sd.created_at, sd.id
                LIMIT 1 FOR UPDATE OF sd SKIP LOCKED;
                """;

        var row = await connection.QuerySingleOrDefaultAsync<DeliveryWorkRow>(
        new CommandDefinition(
            claimSql,
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (row.Status == "in_flight")
        {
            var recoveryCommand = new CommandDefinition(
                sqlServer
                    ? """
                    UPDATE delivery_attempts
                    SET status = 'indeterminate',
                        completed_at = @DatabaseNow,
                        error_message = 'Lease expired before the owning worker finalized this attempt.'
                    WHERE id = @ActiveAttemptId
                      AND event_delivery_id = @Id
                      AND status = 'in_progress';
                    SELECT @@ROWCOUNT;
                    """
                    : """
                    UPDATE delivery_attempts
                    SET status = 'indeterminate',
                        completed_at = @DatabaseNow,
                        error_message = 'Lease expired before the owning worker finalized this attempt.'
                    WHERE id = @ActiveAttemptId
                      AND event_delivery_id = @Id
                      AND status = 'in_progress';
                    """,
                row,
                transaction,
                cancellationToken: cancellationToken);

            int finalized = sqlServer
                ? await connection.ExecuteScalarAsync<int>(recoveryCommand)
                : await connection.ExecuteAsync(recoveryCommand);

            if (finalized != 1)
                throw new InvalidOperationException($"Active attempt {row.ActiveAttemptId} for delivery {row.Id} could not be made indeterminate.");

            DeliveryOutcomeDecision recoveryDecision = outcomePolicy.Decide(
                DeliveryOutcomeKind.Indeterminate,
                row.RetryCycleAttemptCount,
                row.DatabaseNow);

            if (recoveryDecision.Disposition == EventDeliveryDisposition.DeadLettered)
            {
                await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                        UPDATE event_deliveries
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
                return new RecoveredEventDeliveryDeadLetter(
                    row.Id,
                    row.ActiveAttemptId!.Value,
                    row.LifetimeAttemptCount,
                    row.EventId,
                    row.SubscriptionId,
                    row.ConnectorKey ?? string.Empty);
            }
        }

        Guid attemptId = Guid.NewGuid();
        int attemptNumber = row.LifetimeAttemptCount + 1;
        int retryCycleAttemptCount = row.RetryCycleAttemptCount + 1;
        string leaseEnd = sqlServer
            ? "DATEADD(millisecond, @LeaseDurationMilliseconds, @DatabaseNow)"
            : "@DatabaseNow + @LeaseDuration";

        await connection.ExecuteAsync(
        new CommandDefinition(
            $"""
                INSERT INTO delivery_attempts (
                    id,
                    event_delivery_id,
                    attempt_number,
                    status,
                    started_at)
                VALUES (
                    @AttemptId,
                    @DeliveryId,
                    @AttemptNumber,
                    'in_progress',
                    @DatabaseNow);

                UPDATE event_deliveries
                SET status = 'in_flight',
                    lifetime_attempt_count = @AttemptNumber,
                    retry_cycle_attempt_count = @RetryCycleAttemptCount,
                    active_attempt_id = @AttemptId,
                    lease_expires_at = {leaseEnd},
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
                LeaseDurationMilliseconds = (int)options.LeaseDuration.TotalMilliseconds,
                options.LeaseDuration,
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return new ClaimedEventDelivery(new EventDeliveryWorkItem(
            row.Id,
            attemptId,
            attemptNumber,
            row.EventId,
            row.SubscriptionId,
            row.DestinationConnectionId,
            row.TenantId,
            row.TenantSlug,
            row.PayloadJson ?? string.Empty,
            row.EventType ?? string.Empty,
            row.TopicName,
            row.AcceptedAt,
            row.MappingConfigSnapshot,
            row.ConnectorKey ?? string.Empty,
            row.HttpExecutionSnapshotJson ?? string.Empty,
            row.Traceparent));
    }

    public async Task<DeliveryFinalizationResult> FinalizeAsync(
        DeliveryAttemptCompletion completion,
        CancellationToken cancellationToken)
    {
        ValidateCompletion(completion);

        for (int retry = 0; ; retry++)
        {
            try
            {
                return await FinalizeOnceAsync(completion, cancellationToken);
            }
            catch (Exception exception) when (
                IsTransient(exception)
                && retry < FinalizationRetryDelays.Length
                && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(FinalizationRetryDelays[retry], cancellationToken);
            }
        }
    }

    private async Task<DeliveryFinalizationResult> FinalizeOnceAsync(
        DeliveryAttemptCompletion completion,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;

        var owner = await connection.QuerySingleOrDefaultAsync<DeliveryOwnerRow>(
            new CommandDefinition(
                sqlServer
                ? """
                SELECT id AS Id, status AS Status, active_attempt_id AS ActiveAttemptId,
                       retry_cycle_attempt_count AS RetryCycleAttemptCount, SYSUTCDATETIME() AS DatabaseNow
                FROM event_deliveries WITH (UPDLOCK, ROWLOCK)
                WHERE id = @DeliveryId;
                """
                : """
                SELECT
                    id AS Id,
                    status AS Status,
                    active_attempt_id AS ActiveAttemptId,
                    retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    now() AS DatabaseNow
                FROM event_deliveries
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
            owner.DatabaseNow,
            isTerminal: completion.IsTerminalFailure,
            retryAfter: completion.RetryAfter);

        var finalizeCommand = new CommandDefinition(
            sqlServer
                ? """
                UPDATE delivery_attempts
                SET status = @AttemptStatus, failure_phase = @FailurePhase,
                    request_payload = @RequestPayloadJson, response_status_code = @ResponseStatusCode,
                    response_body = @ResponseBody, error_message = @ErrorMessage, completed_at = @DatabaseNow
                WHERE id = @AttemptId AND event_delivery_id = @DeliveryId AND status = N'in_progress';
                SELECT @@ROWCOUNT;
                """
                : """
                UPDATE delivery_attempts
                SET status = @AttemptStatus,
                    failure_phase = @FailurePhase,
                    request_payload = CAST(@RequestPayloadJson AS jsonb),
                    response_status_code = @ResponseStatusCode,
                    response_body = @ResponseBody,
                    error_message = @ErrorMessage,
                    completed_at = @DatabaseNow
                WHERE id = @AttemptId
                  AND event_delivery_id = @DeliveryId
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
            cancellationToken: cancellationToken);

        int finalized = sqlServer
            ? await connection.ExecuteScalarAsync<int>(finalizeCommand)
            : await connection.ExecuteAsync(finalizeCommand);

        if (finalized != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(DeliveryFinalizationStatus.OwnershipLost);
        }

        string deliveryStatus = decision.Disposition switch
        {
            EventDeliveryDisposition.Succeeded => "succeeded",
            EventDeliveryDisposition.RetryScheduled => "pending",
            EventDeliveryDisposition.DeadLettered => "dead_lettered",
            _ => throw new ArgumentOutOfRangeException()
        };

        int advanced = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE event_deliveries
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

    private static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException postgres => postgres.IsTransient,
        SqlException sqlServer => sqlServer.IsTransient,
        _ => false,
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
        public string TenantSlug { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int LifetimeAttemptCount { get; init; }
        public int RetryCycleAttemptCount { get; init; }
        public Guid? ActiveAttemptId { get; init; }
        public string? PayloadJson { get; init; }
        public string? EventType { get; init; }
        public string? TopicName { get; init; }
        public DateTimeOffset AcceptedAt { get; init; }
        public string? MappingConfigSnapshot { get; init; }
        public string? ConnectorKey { get; init; }
        public string? HttpExecutionSnapshotJson { get; init; }
        public string? Traceparent { get; init; }
        public DateTimeOffset DatabaseNow { get; init; }
    }
}
