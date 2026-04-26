using System.Text.Json;
using Dapper;
using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Data;

public sealed class SubscriptionRepository(IDbConnectionFactory connectionFactory) : ISubscriptionRepository
{
    public async Task<Guid?> FindTopicIdAsync(
        Guid tenantId,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT id
                FROM topics
                WHERE tenant_id = @TenantId
                  AND status    = 'active'
                  AND @EventType = ANY(event_types)
                LIMIT 1
                """,
                new { TenantId = tenantId, EventType = eventType },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SubscriptionTarget>> GetActiveSubscriptionsAsync(
        Guid topicId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<SubscriptionRow>(
            new CommandDefinition(
                """
                SELECT
                    s.id                        AS Id,
                    s.name                      AS Name,
                    s.match_rules::text         AS MatchRulesJson,
                    s.destination_connection_id AS DestinationConnectionId,
                    c.config->>'url'            AS DestinationUrl
                FROM subscriptions s
                JOIN connections c ON c.id = s.destination_connection_id
                WHERE s.topic_id = @TopicId
                  AND s.status   = 'active'
                ORDER BY s.order_index
                """,
                new { TopicId = topicId },
                cancellationToken: cancellationToken));

        return rows.Select(s => new SubscriptionTarget(
            s.Id,
            s.Name,
            ParseMatchEventTypes(s.MatchRulesJson),
            s.DestinationConnectionId,
            s.DestinationUrl ?? "")).ToList();
    }

    private static string[] ParseMatchEventTypes(string? matchRulesJson)
    {
        if (string.IsNullOrWhiteSpace(matchRulesJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(matchRulesJson);
            if (doc.RootElement.TryGetProperty("event_types", out var arr))
                return arr.EnumerateArray()
                          .Select(e => e.GetString() ?? "")
                          .Where(s => s.Length > 0)
                          .ToArray();
        }
        catch (JsonException) { }

        return [];
    }

    private sealed record SubscriptionRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? MatchRulesJson { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public string? DestinationUrl { get; init; }
    }
}
