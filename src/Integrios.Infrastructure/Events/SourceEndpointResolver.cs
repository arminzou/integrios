using System.Text.Json;
using Dapper;
using Integrios.Application.Events;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class SourceEndpointResolver(IDbConnectionFactory connectionFactory)
    : ISourceEndpointResolver
{
    public async Task<ResolvedSourceEndpoint?> ResolveAsync(
        string connectorKey,
        Guid endpointId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        // No Source entity exists yet to pick a source_contracts[] entry by key (that lands with the
        // Source model rewrite), so this transitionally takes the first entry: today only one
        // webhook-capable entry is ever configured per Connector.
        string sql = sqlServer
            ? """
                SELECT TOP (1)
                    se.tenant_id AS TenantId, t.slug AS TenantSlug, se.topic_id AS TopicId,
                    se.connection_id AS ConnectionId, i.[key] AS ConnectorKey,
                    JSON_VALUE(i.manifest, '$.source_contracts[0].key') AS SourceAdapterKey,
                    TRY_CONVERT(int, JSON_VALUE(i.manifest, '$.source_contracts[0].contract_version')) AS SourceAdapterContractVersion,
                    JSON_QUERY(i.manifest, '$.source_contracts[0].config') AS SourceAdapterConfigJson,
                    c.source_verification AS SourceVerificationJson
                FROM source_endpoints se
                JOIN topic_sources ts ON ts.tenant_id=se.tenant_id AND ts.topic_id=se.topic_id AND ts.connection_id=se.connection_id
                JOIN connections c ON c.tenant_id=se.tenant_id AND c.id=se.connection_id
                JOIN connectors i ON i.id=c.connector_id
                JOIN tenants t ON t.id=se.tenant_id
                WHERE se.id=@EndpointId AND i.[key]=@ConnectorKey AND se.status=N'active'
                  AND ts.status=N'active' AND c.status=N'active' AND i.status=N'active'
                """
            : """
                SELECT
                    se.tenant_id AS TenantId,
                    t.slug AS TenantSlug,
                    se.topic_id AS TopicId,
                    se.connection_id AS ConnectionId,
                    i.key AS ConnectorKey,
                    i.manifest -> 'source_contracts' -> 0 ->> 'key' AS SourceAdapterKey,
                    (i.manifest -> 'source_contracts' -> 0 ->> 'contract_version')::int AS SourceAdapterContractVersion,
                    (i.manifest -> 'source_contracts' -> 0 -> 'config')::text AS SourceAdapterConfigJson,
                    c.source_verification::text AS SourceVerificationJson
                FROM source_endpoints se
                JOIN topic_sources ts
                  ON ts.tenant_id = se.tenant_id AND ts.topic_id = se.topic_id AND ts.connection_id = se.connection_id
                JOIN connections c ON c.tenant_id = se.tenant_id AND c.id = se.connection_id
                JOIN connectors i ON i.id = c.connector_id
                JOIN tenants t ON t.id = se.tenant_id
                WHERE se.id = @EndpointId AND i.key = @ConnectorKey AND se.status = 'active'
                  AND ts.status = 'active' AND c.status = 'active' AND i.status = 'active'
                LIMIT 1
                """;
        EndpointRow? row = await connection.QuerySingleOrDefaultAsync<EndpointRow>(
            new CommandDefinition(
                sql,
                new { EndpointId = endpointId, ConnectorKey = connectorKey },
                cancellationToken: cancellationToken));

        return row?.ToResolvedSourceEndpoint();
    }

    private sealed record EndpointRow
    {
        public Guid TenantId { get; init; }
        public string TenantSlug { get; init; } = "";
        public Guid TopicId { get; init; }
        public Guid ConnectionId { get; init; }
        public string ConnectorKey { get; init; } = "";
        public string? SourceAdapterKey { get; init; }
        public int? SourceAdapterContractVersion { get; init; }
        public string? SourceAdapterConfigJson { get; init; }
        public string? SourceVerificationJson { get; init; }

        public ResolvedSourceEndpoint? ToResolvedSourceEndpoint()
        {
            if (SourceAdapterKey is null || SourceAdapterContractVersion is null || SourceVerificationJson is null)
                return null;

            return new ResolvedSourceEndpoint
            {
                TenantId = TenantId,
                TenantSlug = TenantSlug,
                TopicId = TopicId,
                ConnectionId = ConnectionId,
                ConnectorKey = ConnectorKey,
                SourceAdapterKey = SourceAdapterKey,
                SourceAdapterContractVersion = SourceAdapterContractVersion.Value,
                SourceAdapterConfig = JsonSerializer.Deserialize<JsonElement>(SourceAdapterConfigJson ?? "{}"),
                SourceVerification = JsonSerializer.Deserialize<ConnectionSchemeSelection>(
                    SourceVerificationJson, ConnectionSchemeSelection.StoredJson)!,
            };
        }
    }
}
