using Dapper;
using Integrios.Application.Events;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class IntakeTopicResolver(IDbConnectionFactory connectionFactory)
    : ISourceTopicLookup
{
    public async Task<Guid?> FindActiveSourceTopicAsync(
        Guid tenantId,
        string topicName,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string top = sqlServer ? "TOP (1) " : string.Empty;
        string limit = sqlServer ? string.Empty : "LIMIT 1";
        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                $"""
                SELECT {top}t.id
                FROM topics t
                JOIN sources s
                  ON s.tenant_id = t.tenant_id
                 AND s.topic_id = t.id
                JOIN connections c
                  ON c.tenant_id = s.tenant_id
                 AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                WHERE t.tenant_id = @TenantId
                  AND t.name = @TopicName
                  AND t.status = 'active'
                  AND s.id = @SourceId
                  AND s.status = 'active'
                  AND c.status = 'active'
                  AND i.status = 'active'
                  AND i.direction IN ('source', 'both')
                {limit}
                """,
                new { TenantId = tenantId, TopicName = topicName, SourceId = sourceId },
                cancellationToken: cancellationToken));
    }
}
