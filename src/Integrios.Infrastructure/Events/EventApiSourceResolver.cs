using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class EventApiSourceResolver(IDbConnectionFactory connectionFactory)
    : IEventApiSourceResolver
{
    public async Task<ResolvedEventApiSource?> ResolveAsync(
        Guid tenantId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string sql = sqlServer
            ? """
                SELECT TOP (1)
                    s.topic_id AS TopicId
                FROM sources s
                JOIN connectors i ON i.id = s.connector_id
                WHERE s.tenant_id = @TenantId AND s.id = @SourceId
                  AND s.type = N'event_api' AND s.status = N'active'
                  AND i.status = N'active' AND i.direction IN (N'source', N'both')
                """
            : """
                SELECT
                    s.topic_id AS TopicId
                FROM sources s
                JOIN connectors i ON i.id = s.connector_id
                WHERE s.tenant_id = @TenantId AND s.id = @SourceId
                  AND s.type = 'event_api' AND s.status = 'active'
                  AND i.status = 'active' AND i.direction IN ('source', 'both')
                LIMIT 1
                """;

        SourceRow? row = await connection.QuerySingleOrDefaultAsync<SourceRow>(
            new CommandDefinition(
                sql,
                new { TenantId = tenantId, SourceId = sourceId },
                cancellationToken: cancellationToken));

        return row?.ToResolvedEventApiSource();
    }

    private sealed record SourceRow
    {
        public Guid TopicId { get; init; }
        public ResolvedEventApiSource ToResolvedEventApiSource() => new()
        {
            TopicId = TopicId,
            SourceContractSchema = null,
            SourceMapping = null,
        };
    }
}
