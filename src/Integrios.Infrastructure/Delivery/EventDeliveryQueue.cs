using Dapper;
using Integrios.Application.Delivery;
using Integrios.Application.DeliveryApi;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Integrios.Infrastructure.Delivery;

internal sealed class EventDeliveryQueue(
    IDbConnectionFactory connectionFactory,
    DeliveryExecutionOptions options,
    DeliveryOutcomePolicy outcomePolicy) : IEventDeliveryQueue, IPullDeliveryQueue
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
        CancellationToken cancellationToken) =>
        await ClaimRowAsync(null, cancellationToken) switch
        {
            ClaimedRow claimed => new ClaimedEventDelivery(new EventDeliveryWorkItem(
                claimed.Row.Id,
                claimed.AttemptId,
                claimed.AttemptNumber,
                claimed.Row.EventId,
                claimed.Row.SubscriptionId,
                claimed.Row.DestinationId,
                claimed.Row.TenantId,
                claimed.Row.TenantSlug,
                claimed.Row.PayloadJson ?? string.Empty,
                claimed.Row.EventType ?? string.Empty,
                claimed.Row.TopicName,
                claimed.Row.AcceptedAt,
                claimed.Row.MappingConfigSnapshot,
                claimed.Row.ConnectorKey ?? string.Empty,
                claimed.Row.HttpExecutionSnapshotJson ?? string.Empty,
                claimed.Row.Traceparent)),
            var other => other
        };

    public async Task<EventDeliveryClaimResult?> ClaimNextAsync(Guid destinationId, CancellationToken cancellationToken) =>
        await ClaimRowAsync(destinationId, cancellationToken) switch
        {
            ClaimedRow claimed => new ClaimedPullDelivery(new PullDeliveryWorkItem(
                claimed.Row.Id,
                claimed.AttemptId,
                claimed.AttemptNumber,
                claimed.Row.EventId,
                claimed.Row.SubscriptionId,
                claimed.Row.PayloadJson ?? string.Empty,
                claimed.Row.EventType ?? string.Empty,
                claimed.Row.TopicName,
                claimed.Row.AcceptedAt,
                claimed.Row.MappingConfigSnapshot,
                claimed.LeaseExpiresAt,
                claimed.Row.Traceparent)),
            var other => other
        };

    private async Task<EventDeliveryClaimResult?> ClaimRowAsync(
        Guid? pullDestinationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string databaseNow = sqlServer ? "SYSUTCDATETIME()" : "now()";
        string claimable = EventDeliveryClaimability.Predicate(connectionFactory.Provider, "sd", databaseNow);
        string scope = (pullDestinationId, sqlServer) switch
        {
            (null, true) => "sd.transport=N'http'",
            (null, false) => "sd.transport = 'http'",
            (_, true) => "sd.transport=N'pull' AND sd.destination_id=@DestinationId",
            (_, false) => "sd.transport = 'pull' AND sd.destination_id = @DestinationId",
        };
        string claimSql = sqlServer
            ? $"""
                SELECT TOP (1)
                    sd.id AS Id, sd.event_id AS EventId, sd.subscription_id AS SubscriptionId,
                    sd.destination_id AS DestinationId, sd.status AS Status,
                    sd.lifetime_attempt_count AS LifetimeAttemptCount,
                    sd.retry_cycle_attempt_count AS RetryCycleAttemptCount,
                    sd.active_attempt_id AS ActiveAttemptId, sd.connector_key AS ConnectorKey,
                    sd.http_execution_snapshot AS HttpExecutionSnapshotJson,
                    sd.mapping_config_snapshot AS MappingConfigSnapshot, sd.traceparent AS Traceparent,
                    e.tenant_id AS TenantId, tenant.slug AS TenantSlug, e.payload AS PayloadJson,
                    e.event_type AS EventType, e.accepted_at AS AcceptedAt, t.[key] AS TopicName,
                    TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS DatabaseNow
                FROM event_deliveries sd WITH (UPDLOCK, ROWLOCK, READPAST, READCOMMITTEDLOCK)
                JOIN events e ON e.id=sd.event_id
                JOIN tenants tenant ON tenant.id=e.tenant_id
                LEFT JOIN topics t ON t.id=e.topic_id
                WHERE {scope} AND ({claimable})
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
                    sd.destination_id AS DestinationId,
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
                    t.key AS TopicName,
                    now() AS DatabaseNow
                FROM event_deliveries sd
                JOIN events e ON e.id = sd.event_id
                JOIN tenants tenant ON tenant.id = e.tenant_id
                LEFT JOIN topics t ON t.id = e.topic_id
                WHERE {scope} AND ({claimable})
                ORDER BY CASE WHEN sd.status = 'in_flight' THEN 0 ELSE 1 END,
                    sd.lease_expires_at NULLS LAST, sd.deliver_after NULLS FIRST, sd.created_at, sd.id
                LIMIT 1 FOR UPDATE OF sd SKIP LOCKED;
                """;

        var row = await connection.QuerySingleOrDefaultAsync<DeliveryWorkRow>(
        new CommandDefinition(
            claimSql,
            new { DestinationId = pullDestinationId },
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

        return new ClaimedRow(row, attemptId, attemptNumber, row.DatabaseNow + options.LeaseDuration);
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
                       retry_cycle_attempt_count AS RetryCycleAttemptCount,
                       TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS DatabaseNow
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
                SET status = @AttemptStatus, failure_phase = @FailurePhase, terminal_failure = @TerminalFailure,
                    request_payload = @RequestPayloadJson, response_status_code = @ResponseStatusCode,
                    response_body = @ResponseBody, response_body_truncated = @ResponseBodyTruncated,
                    error_message = @ErrorMessage, completed_at = @DatabaseNow
                WHERE id = @AttemptId AND event_delivery_id = @DeliveryId AND status = N'in_progress';
                SELECT @@ROWCOUNT;
                """
                : """
                UPDATE delivery_attempts
                SET status = @AttemptStatus,
                    failure_phase = @FailurePhase,
                    terminal_failure = @TerminalFailure,
                    request_payload = CAST(@RequestPayloadJson AS jsonb),
                    response_status_code = @ResponseStatusCode,
                    response_body = @ResponseBody,
                    response_body_truncated = @ResponseBodyTruncated,
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
                    TerminalFailure = completion.Succeeded ? (bool?)null : completion.IsTerminalFailure,
                    completion.RequestPayloadJson,
                    completion.ResponseStatusCode,
                    completion.ResponseBody,
                    completion.ResponseBodyTruncated,
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

    public async Task<PullLeaseRenewalResult> RenewAsync(PullAttemptOwner owner, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;

        var lease = await connection.QuerySingleOrDefaultAsync<PullLeaseRow>(
            new CommandDefinition(
                sqlServer
                ? """
                SELECT d.status AS Status, d.active_attempt_id AS ActiveAttemptId, a.started_at AS StartedAt,
                       TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS DatabaseNow
                FROM event_deliveries d WITH (UPDLOCK, ROWLOCK)
                LEFT JOIN delivery_attempts a ON a.id = d.active_attempt_id AND a.event_delivery_id = d.id
                WHERE d.id = @DeliveryId AND d.destination_id = @DestinationId AND d.transport = N'pull';
                """
                : """
                SELECT d.status AS Status, d.active_attempt_id AS ActiveAttemptId, a.started_at AS StartedAt,
                       now() AS DatabaseNow
                FROM event_deliveries d
                LEFT JOIN delivery_attempts a ON a.id = d.active_attempt_id AND a.event_delivery_id = d.id
                WHERE d.id = @DeliveryId AND d.destination_id = @DestinationId AND d.transport = 'pull'
                FOR UPDATE OF d;
                """,
                owner,
                transaction,
                cancellationToken: cancellationToken));

        if (lease is null || lease.Status != "in_flight" || lease.ActiveAttemptId != owner.AttemptId
            || lease.StartedAt is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(PullLeaseRenewalStatus.OwnershipLost, null);
        }

        DateTimeOffset ceiling = lease.StartedAt.Value + options.MaxAttemptDuration;
        if (lease.DatabaseNow >= ceiling)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(PullLeaseRenewalStatus.MaxAttemptDurationReached, null);
        }

        DateTimeOffset leaseExpiresAt = lease.DatabaseNow + options.LeaseDuration;
        if (leaseExpiresAt > ceiling)
            leaseExpiresAt = ceiling;

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE event_deliveries
                SET lease_expires_at = @LeaseExpiresAt, updated_at = @DatabaseNow
                WHERE id = @DeliveryId AND active_attempt_id = @AttemptId;
                """,
                new { owner.DeliveryId, owner.AttemptId, LeaseExpiresAt = leaseExpiresAt, lease.DatabaseNow },
                transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return new(PullLeaseRenewalStatus.Renewed, leaseExpiresAt);
    }

    public async Task<PullSettlementStatus> SettleAsync(
        PullAttemptOwner owner,
        PullSettlement settlement,
        CancellationToken cancellationToken)
    {
        PullAttemptState? attempt = await ReadPullAttemptAsync(owner, cancellationToken);
        if (attempt?.Status == "in_progress")
        {
            DeliveryFinalizationResult result = await FinalizeAsync(
                new DeliveryAttemptCompletion(
                    owner.DeliveryId,
                    owner.AttemptId,
                    settlement.Succeeded,
                    settlement.FailurePhase,
                    RequestPayloadJson: null,
                    ResponseStatusCode: null,
                    ResponseBody: null,
                    settlement.ErrorMessage,
                    settlement.IsTerminal,
                    settlement.RetryAfter),
                cancellationToken);
            if (result.Status == DeliveryFinalizationStatus.Applied)
                return PullSettlementStatus.Applied;

            attempt = await ReadPullAttemptAsync(owner, cancellationToken);
        }

        return attempt?.Status == (settlement.Succeeded ? "succeeded" : "failed")
            && (settlement.Succeeded || attempt.TerminalFailure == settlement.IsTerminal)
            ? PullSettlementStatus.AlreadyApplied
            : PullSettlementStatus.OwnershipLost;
    }

    private async Task<PullAttemptState?> ReadPullAttemptAsync(PullAttemptOwner owner, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PullAttemptState>(
            new CommandDefinition(
                """
                SELECT a.status AS Status, a.terminal_failure AS TerminalFailure
                FROM event_deliveries d
                JOIN delivery_attempts a ON a.event_delivery_id = d.id
                WHERE d.id = @DeliveryId AND d.destination_id = @DestinationId AND d.transport = 'pull'
                  AND a.id = @AttemptId;
                """,
                owner,
                cancellationToken: cancellationToken));
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
        DeliveryFailurePhase.Authentication => "authentication",
        DeliveryFailurePhase.RequestConstruction => "request_construction",
        DeliveryFailurePhase.Http => "http",
        DeliveryFailurePhase.Subscriber => "subscriber",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    // SqlClient does not override DbException.IsTransient, so it reads false for every SqlException.
    // A deadlock victim (1205) is the contention fault a finalization transaction can hit.
    private static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException postgres => postgres.IsTransient,
        SqlException { Number: 1205 } => true,
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

    private sealed record PullAttemptState(string Status, bool? TerminalFailure);

    private sealed record ClaimedRow(
        DeliveryWorkRow Row,
        Guid AttemptId,
        int AttemptNumber,
        DateTimeOffset LeaseExpiresAt) : EventDeliveryClaimResult;

    private sealed record PullLeaseRow
    {
        public string Status { get; init; } = string.Empty;
        public Guid? ActiveAttemptId { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset DatabaseNow { get; init; }
    }

    private sealed record DeliveryWorkRow
    {
        public Guid Id { get; init; }
        public Guid EventId { get; init; }
        public Guid SubscriptionId { get; init; }
        public Guid DestinationId { get; init; }
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
