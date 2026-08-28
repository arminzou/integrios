using System.Text.Json;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
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
        activity?.SetTag("integrios.event.id", row.EventId);
        activity?.SetTag("integrios.topic.id", row.TopicId);

        IReadOnlyList<SubscriptionRoutingCandidate> candidates = row.TopicId is null
            ? []
            : await LoadCandidatesAsync(context, row.TopicId.Value, cancellationToken);
        IReadOnlyList<SubscriptionFanoutTarget> targets =
            SubscriptionRoutingEvaluator.SelectTargets(row.EventType, candidates);
        EventStatus status = targets.Count == 0 ? EventStatus.Unrouted : EventStatus.Routed;
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
        Guid[] connectorIds = connections.Values.Select(connection => connection.ConnectorId).Distinct().ToArray();
        var connectors = await context.Connectors.AsNoTracking()
            .Where(connector => connectorIds.Contains(connector.Id))
            .ToDictionaryAsync(connector => connector.Id, cancellationToken);

        return subscriptions.Select(subscription =>
        {
            Connection connection = connections[subscription.DestinationConnectionId];
            var connector = connectors[connection.ConnectorId];
            string baseUri = connection.Config.TryGetProperty("base_uri", out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
            var snapshot = new HttpExecutionSnapshot
            {
                Version = HttpExecutionSnapshot.CurrentVersion,
                BaseUri = baseUri,
                Request = subscription.HttpDelivery,
                DestinationAuthentication = connection.DestinationAuthentication,
                HttpSuccess = connector.Manifest.HttpSuccess is { } httpSuccess
                    ? JsonSerializer.Deserialize<HttpSuccessRule>(httpSuccess.GetRawText(), StoredJson.Options)
                    : null,
            };
            return new SubscriptionRoutingCandidate(
                subscription.Id,
                subscription.DestinationConnectionId,
                subscription.OrderIndex,
                subscription.MatchRules.GetRawText(),
                subscription.MappingConfig?.GetRawText(),
                connector.Key,
                JsonSerializer.Serialize(snapshot, StoredJson.Options));
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
                MERGE event_deliveries WITH (HOLDLOCK) AS target
                USING (VALUES (@EventId, @SubscriptionId)) AS source(event_id, subscription_id)
                   ON target.event_id = source.event_id AND target.subscription_id = source.subscription_id
                WHEN NOT MATCHED THEN
                    INSERT (event_id, subscription_id, destination_connection_id, connector_key,
                            http_execution_snapshot, mapping_config_snapshot, traceparent)
                    VALUES (@EventId, @SubscriptionId, @DestinationConnectionId, @ConnectorKey,
                            @HttpExecutionSnapshotJson, @MappingConfigJson, @Traceparent)
                OUTPUT CASE WHEN $action = 'INSERT' THEN 1 ELSE 0 END;
                """,
                new
                {
                    EventId = eventId,
                    target.SubscriptionId,
                    target.DestinationConnectionId,
                    target.ConnectorKey,
                    target.HttpExecutionSnapshotJson,
                    target.MappingConfigJson,
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
