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
        Guid sourceConnectionId,
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
                JOIN topic_sources ts
                  ON ts.tenant_id = t.tenant_id
                 AND ts.topic_id = t.id
                JOIN connections c
                  ON c.tenant_id = ts.tenant_id
                 AND c.id = ts.connection_id
                JOIN integrations i ON i.id = c.integration_id
                WHERE t.tenant_id = @TenantId
                  AND t.name = @TopicName
                  AND t.status = 'active'
                  AND ts.status = 'active'
                  AND c.id = @SourceConnectionId
                  AND c.status = 'active'
                  AND i.status = 'active'
                  AND i.direction IN ('source', 'both')
                {limit}
                """,
                new { TenantId = tenantId, TopicName = topicName, SourceConnectionId = sourceConnectionId },
                cancellationToken: cancellationToken));
    }
}
