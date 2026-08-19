using System.Text.Json;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using Integrios.Application.Telemetry;
using Integrios.Domain.Common;
using Integrios.Domain.Connections;
using Integrios.Domain.Delivery;
using Integrios.Domain.Events;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Integrios.Infrastructure.Outbox;

internal sealed class SqlServerOutboxFanout(IDbContextFactory<IntegriosDbContext> contextFactory) : IOutboxFanout
{
    public async Task<OutboxFanoutResult?> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();
        var row = await connection.QuerySingleOrDefaultAsync<OutboxFanoutRow>(new CommandDefinition(
            """
            SELECT TOP (1)
                o.id AS OutboxId, o.traceparent AS Traceparent,
                e.id AS EventId, e.event_type AS EventType, e.topic_id AS TopicId
            FROM outbox o WITH (UPDLOCK, ROWLOCK, READPAST, READCOMMITTEDLOCK)
            JOIN events e ON e.id = o.event_id
            WHERE o.processed_at IS NULL
              AND (o.deliver_after IS NULL OR o.deliver_after <= SYSUTCDATETIME())
            ORDER BY CASE WHEN o.deliver_after IS NULL THEN 0 ELSE 1 END,
                     o.deliver_after, o.created_at, o.id
            """,
            transaction: dbTransaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        using var activity = ActivitySources.StartLinkedSpan("outbox.fanout", row.Traceparent);
        activity?.SetTag("event_id", row.EventId);
        activity?.SetTag("topic_id", row.TopicId);

        IReadOnlyList<SubscriptionRoutingCandidate> candidates = row.TopicId is null
            ? []
            : await LoadCandidatesAsync(context, row.TopicId.Value, cancellationToken);
        IReadOnlyList<SubscriptionFanoutTarget> targets =
            SubscriptionRoutingEvaluator.SelectTargets(row.EventType, candidates);
        EventStatus status = targets.Count == 0 ? EventStatus.Unrouted : EventStatus.FannedOut;
        int insertedCount = await InsertDeliveriesAsync(
            connection, dbTransaction, row.EventId, targets, activity?.Id, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE events
            SET status = @Status,
                processed_at = CASE WHEN @Status = N'unrouted' THEN SYSUTCDATETIME() ELSE processed_at END
            WHERE id = @EventId;
            UPDATE outbox SET processed_at = SYSUTCDATETIME() WHERE id = @OutboxId;
            """,
            new { row.EventId, row.OutboxId, Status = EventStatusMap.ToDbValue(status) },
            dbTransaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new OutboxFanoutResult(row.EventId, row.TopicId, status, targets.Count, insertedCount);
    }

    private static async Task<IReadOnlyList<SubscriptionRoutingCandidate>> LoadCandidatesAsync(
        IntegriosDbContext context,
        Guid topicId,
        CancellationToken cancellationToken)
    {
        var subscriptions = await context.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.TopicId == topicId && subscription.Status == OperationalStatus.Active)
            .ToListAsync(cancellationToken);
        Guid[] connectionIds = subscriptions.Select(subscription => subscription.DestinationConnectionId).Distinct().ToArray();
        var connections = await context.Connections.AsNoTracking()
            .Where(connection => connectionIds.Contains(connection.Id))
            .ToDictionaryAsync(connection => connection.Id, cancellationToken);
        Guid[] integrationIds = connections.Values.Select(connection => connection.IntegrationId).Distinct().ToArray();
        var integrations = await context.Integrations.AsNoTracking()
            .Where(integration => integrationIds.Contains(integration.Id))
            .ToDictionaryAsync(integration => integration.Id, cancellationToken);

        return subscriptions.Select(subscription =>
        {
            Connection connection = connections[subscription.DestinationConnectionId];
            var integration = integrations[connection.IntegrationId];
            string baseUri = connection.Config.TryGetProperty("base_uri", out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
            var snapshot = new HttpExecutionSnapshot
            {
                Version = HttpExecutionSnapshot.CurrentVersion,
                BaseUri = baseUri,
                Request = subscription.HttpDelivery,
                DestinationAuthentication = connection.DestinationAuthentication,
                HttpOutcome = integration.Manifest.HttpOutcome is { } outcome
                    ? JsonSerializer.Deserialize<HttpOutcomeContract>(outcome.GetRawText(), ConnectionSchemeSelection.StoredJson)
                    : null,
            };
            return new SubscriptionRoutingCandidate(
                subscription.Id,
                subscription.DestinationConnectionId,
                subscription.OrderIndex,
                subscription.MatchRules.GetRawText(),
                subscription.TransformConfig?.GetRawText(),
                integration.Key,
                JsonSerializer.Serialize(snapshot, ConnectionSchemeSelection.StoredJson));
        }).ToList();
    }

    private static async Task<int> InsertDeliveriesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid eventId,
        IReadOnlyList<SubscriptionFanoutTarget> targets,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        int inserted = 0;
        foreach (SubscriptionFanoutTarget target in targets)
        {
            inserted += await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                MERGE subscription_deliveries WITH (HOLDLOCK) AS target
                USING (VALUES (@EventId, @SubscriptionId)) AS source(event_id, subscription_id)
                   ON target.event_id = source.event_id AND target.subscription_id = source.subscription_id
                WHEN NOT MATCHED THEN
                    INSERT (event_id, subscription_id, destination_connection_id, integration_key,
                            http_execution_snapshot, transform_config_snapshot, traceparent)
                    VALUES (@EventId, @SubscriptionId, @DestinationConnectionId, @IntegrationKey,
                            @HttpExecutionSnapshotJson, @TransformConfigJson, @Traceparent)
                OUTPUT CASE WHEN $action = 'INSERT' THEN 1 ELSE 0 END;
                """,
                new
                {
                    EventId = eventId,
                    target.SubscriptionId,
                    target.DestinationConnectionId,
                    target.IntegrationKey,
                    target.HttpExecutionSnapshotJson,
                    target.TransformConfigJson,
                    Traceparent = traceparent,
                },
                transaction,
                cancellationToken: cancellationToken));
        }
        return inserted;
    }

    private sealed record OutboxFanoutRow
    {
        public Guid OutboxId { get; init; }
        public Guid EventId { get; init; }
        public string EventType { get; init; } = string.Empty;
        public Guid? TopicId { get; init; }
        public string? Traceparent { get; init; }
    }
}
