using System.Text.Json;
using Dapper;
using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Data;

public sealed class RoutingRepository(IDbConnectionFactory connectionFactory) : IRoutingRepository
{
    public async Task<Guid?> FindPipelineIdAsync(
        Guid tenantId,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT id
                FROM pipelines
                WHERE tenant_id = @TenantId
                  AND status    = 'active'
                  AND @EventType = ANY(event_types)
                LIMIT 1
                """,
                new { TenantId = tenantId, EventType = eventType },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<RouteTarget>> GetActiveRoutesAsync(
        Guid pipelineId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<RouteRow>(
            new CommandDefinition(
                """
                SELECT
                    r.id                          AS Id,
                    r.name                        AS Name,
                    r.match_rules::text           AS MatchRulesJson,
                    r.destination_connection_id   AS DestinationConnectionId,
                    c.config->>'url'              AS DestinationUrl
                FROM routes r
                JOIN connections c ON c.id = r.destination_connection_id
                WHERE r.pipeline_id = @PipelineId
                  AND r.status      = 'active'
                ORDER BY r.order_index
                """,
                new { PipelineId = pipelineId },
                cancellationToken: cancellationToken));

        return rows.Select(r => new RouteTarget(
            r.Id,
            r.Name,
            ParseMatchEventTypes(r.MatchRulesJson),
            r.DestinationConnectionId,
            r.DestinationUrl ?? "")).ToList();
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

    private sealed record RouteRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? MatchRulesJson { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public string? DestinationUrl { get; init; }
    }
}
