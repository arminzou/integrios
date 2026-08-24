using System.Text.Json;
using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.ValueObjects;
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
                    s.topic_id AS TopicId,
                    JSON_VALUE(s.configuration, '$.source_contract') AS SourceContractKey,
                    i.manifest AS ManifestJson
                FROM sources s
                JOIN connections c ON c.tenant_id = s.tenant_id AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                WHERE s.tenant_id = @TenantId AND s.id = @SourceId
                  AND s.type = N'event_api' AND s.status = N'active'
                  AND c.status = N'active'
                  AND i.status = N'active' AND i.direction IN (N'source', N'both')
                """
            : """
                SELECT
                    s.topic_id AS TopicId,
                    s.configuration ->> 'source_contract' AS SourceContractKey,
                    i.manifest::text AS ManifestJson
                FROM sources s
                JOIN connections c ON c.tenant_id = s.tenant_id AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                WHERE s.tenant_id = @TenantId AND s.id = @SourceId
                  AND s.type = 'event_api' AND s.status = 'active'
                  AND c.status = 'active'
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
        public string? SourceContractKey { get; init; }
        public string ManifestJson { get; init; } = "";

        public ResolvedEventApiSource? ToResolvedEventApiSource()
        {
            if (SourceContractKey is null)
                return null;

            ConnectorManifest manifest = JsonSerializer.Deserialize<ConnectorManifest>(
                ManifestJson, StoredJson.Options)!;
            ConnectorSourceContractManifest? contract = manifest.SourceContracts
                .FirstOrDefault(candidate => candidate.Key == SourceContractKey);
            if (contract is null)
                return null;

            return new ResolvedEventApiSource
            {
                TopicId = TopicId,
                SourceContractSchema = contract.Schema,
                SourceMapping = contract.Mapping is { } mapping
                    ? new TransformSpec(mapping.Engine, mapping.Version, mapping.Expression)
                    : null,
            };
        }
    }
}
