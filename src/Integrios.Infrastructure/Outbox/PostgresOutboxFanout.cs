using System.Text.Json;
using Dapper;
using Integrios.Application.Delivery;
using Integrios.Application.Outbox;
using Integrios.Application.Telemetry;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Integrios.Infrastructure.Outbox;

internal sealed class PostgresOutboxFanout(IDbContextFactory<IntegriosDbContext> contextFactory) : IOutboxFanout
{
    public async Task<OutboxFanoutResult?> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using IntegriosDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        var row = await connection.QuerySingleOrDefaultAsync<OutboxFanoutRow>(
            new CommandDefinition(
                """
                SELECT
                    o.id          AS OutboxId,
                    o.traceparent AS Traceparent,
                    e.id          AS EventId,
                    e.event_type  AS EventType,
                    e.topic_id    AS TopicId
                -- Event acceptance inserts the Event and outbox row atomically, and the outbox
                -- foreign key prevents a dangling EventId. A missing Event is database corruption,
                -- not queue work for this adapter to reconcile.
                FROM outbox o
                JOIN events e ON e.id = o.event_id
                WHERE o.processed_at IS NULL
                  AND (o.deliver_after IS NULL OR o.deliver_after <= now())
                ORDER BY o.deliver_after NULLS FIRST, o.created_at, o.id
                LIMIT 1
                FOR UPDATE OF o SKIP LOCKED
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

        // An accepted Event without a Topic has no routing path and reaches the same terminal
        // unrouted state as an Event whose Topic has no matching Subscription.
        var candidates = row.TopicId is null
            ? []
            : await LoadCandidatesAsync(connection, dbTransaction, row.TopicId.Value, cancellationToken);
        var targets = SubscriptionRoutingEvaluator.SelectTargets(row.EventType, candidates);

        var status = targets.Count == 0 ? EventStatus.Unrouted : EventStatus.FannedOut;
        var insertedCount = await InsertDeliveriesAsync(
            connection,
            dbTransaction,
            row.EventId,
            targets,
            activity?.Id,
            cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE events
                SET status = @Status,
                    processed_at = CASE WHEN @Status = 'unrouted' THEN now() ELSE processed_at END
                WHERE id = @EventId
                """,
                new { row.EventId, Status = EventStatusMap.ToDbValue(status) },
                dbTransaction,
                cancellationToken: cancellationToken));

        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE outbox SET processed_at = now() WHERE id = @OutboxId",
                new { row.OutboxId },
                dbTransaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return new OutboxFanoutResult(row.EventId, row.TopicId, status, targets.Count, insertedCount);
    }

    private static async Task<IReadOnlyList<SubscriptionRoutingCandidate>> LoadCandidatesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid topicId,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<SubscriptionCandidateRow>(
            new CommandDefinition(
                """
                SELECT
                    s.id AS SubscriptionId,
                    s.destination_connection_id AS DestinationConnectionId,
                    s.order_index AS OrderIndex,
                    s.match_rules::text AS MatchRulesJson,
                    s.transform_config::text AS TransformConfigJson,
                    s.http_delivery::text AS HttpDeliveryJson,
                    COALESCE(c.config->>'base_uri', '') AS DestinationUrl,
                    i.key AS ConnectorKey,
                    c.destination_authentication::text AS DestinationAuthJson,
                    (i.manifest -> 'http_success')::text AS HttpSuccessJson
                FROM subscriptions s
                JOIN connections c ON c.id = s.destination_connection_id
                JOIN connectors i ON i.id = c.connector_id
                WHERE s.topic_id = @TopicId
                  AND s.status = 'active'
                """,
                new { TopicId = topicId },
                transaction,
                cancellationToken: cancellationToken));

        return rows
            .Select(row => new SubscriptionRoutingCandidate(
                row.SubscriptionId,
                row.DestinationConnectionId,
                row.OrderIndex,
                row.MatchRulesJson,
                row.TransformConfigJson,
                row.ConnectorKey,
                BuildHttpExecutionSnapshotJson(
                    row.DestinationUrl, row.HttpDeliveryJson, row.DestinationAuthJson, row.HttpSuccessJson)))
            .ToList();
    }

    // Fanout correlates the base_uri, request shape, destination authentication, and effective HTTP
    // success rule a delivery will be dispatched and retried with, so a later Subscription,
    // Connection, or Connector edit cannot change an in-flight delivery's request or success
    // criteria out from under it.
    private static string BuildHttpExecutionSnapshotJson(
        string destinationUrl, string httpDeliveryJson, string? destinationAuthJson, string? httpSuccessJson)
    {
        var snapshot = new HttpExecutionSnapshot
        {
            Version = HttpExecutionSnapshot.CurrentVersion,
            BaseUri = destinationUrl,
            Request = JsonSerializer.Deserialize<HttpDeliveryConfiguration>(httpDeliveryJson, ConnectionSchemeSelection.StoredJson)
                ?? throw new InvalidOperationException("Stored HTTP delivery configuration is invalid."),
            DestinationAuthentication = string.IsNullOrWhiteSpace(destinationAuthJson)
                ? null
                : JsonSerializer.Deserialize<ConnectionSchemeSelection>(destinationAuthJson, ConnectionSchemeSelection.StoredJson),
            HttpSuccess = string.IsNullOrWhiteSpace(httpSuccessJson) || httpSuccessJson == "null"
                ? null
                : JsonSerializer.Deserialize<HttpSuccessRule>(httpSuccessJson, ConnectionSchemeSelection.StoredJson)
        };
        return JsonSerializer.Serialize(snapshot, ConnectionSchemeSelection.StoredJson);
    }

    private static async Task<int> InsertDeliveriesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid eventId,
        IReadOnlyList<SubscriptionFanoutTarget> targets,
        string? traceparent,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return 0;

        return await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO subscription_deliveries (
                    event_id,
                    subscription_id,
                    destination_connection_id,
                    connector_key,
                    http_execution_snapshot,
                    transform_config_snapshot,
                    traceparent)
                VALUES (
                    @EventId,
                    @SubscriptionId,
                    @DestinationConnectionId,
                    @ConnectorKey,
                    @HttpExecutionSnapshotJson::jsonb,
                    @TransformConfigJson::jsonb,
                    @Traceparent)
                ON CONFLICT (event_id, subscription_id) DO NOTHING
                """,
                targets.Select(target => new
                {
                    EventId = eventId,
                    target.SubscriptionId,
                    target.DestinationConnectionId,
                    target.ConnectorKey,
                    target.HttpExecutionSnapshotJson,
                    target.TransformConfigJson,
                    Traceparent = traceparent
                }),
                transaction,
                cancellationToken: cancellationToken));
    }

    private sealed record OutboxFanoutRow
    {
        public Guid OutboxId { get; init; }
        public Guid EventId { get; init; }
        public string EventType { get; init; } = string.Empty;
        public Guid? TopicId { get; init; }
        public string? Traceparent { get; init; }
    }

    private sealed record SubscriptionCandidateRow
    {
        public Guid SubscriptionId { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public int OrderIndex { get; init; }
        public string? MatchRulesJson { get; init; }
        public string? TransformConfigJson { get; init; }
        public string HttpDeliveryJson { get; init; } = "{}";
        public string DestinationUrl { get; init; } = string.Empty;
        public string ConnectorKey { get; init; } = string.Empty;
        public string? DestinationAuthJson { get; init; }
        public string? HttpSuccessJson { get; init; }
    }
}
