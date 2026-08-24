using System.Text.Json;
using Dapper;
using Integrios.Application.Bootstrap;
using Integrios.Application.Ingestion;
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
        string sql = sqlServer
            ? """
                SELECT TOP (1)
                    s.tenant_id AS TenantId, t.slug AS TenantSlug, s.topic_id AS TopicId, s.id AS SourceId,
                    s.connection_id AS ConnectionId, i.[key] AS ConnectorKey,
                    c.source_verification AS SourceVerificationJson
                FROM sources s
                JOIN connections c ON c.tenant_id=s.tenant_id AND c.id=s.connection_id
                JOIN connectors i ON i.id=c.connector_id
                JOIN tenants t ON t.id=s.tenant_id
                WHERE JSON_VALUE(s.configuration, '$.callback_id')=@EndpointId
                  AND JSON_VALUE(s.configuration, '$.source_contract')=N'github_webhook'
                  AND i.id=@GitHubConnectorId AND i.contract_version=@GitHubContractVersion AND i.[key]=@ConnectorKey
                  AND s.type=N'webhook' AND s.status=N'active'
                  AND c.status=N'active' AND i.status=N'active'
                """
            : """
                SELECT
                    s.tenant_id AS TenantId,
                    t.slug AS TenantSlug,
                    s.topic_id AS TopicId,
                    s.id AS SourceId,
                    s.connection_id AS ConnectionId,
                    i.key AS ConnectorKey,
                    c.source_verification::text AS SourceVerificationJson
                FROM sources s
                JOIN connections c ON c.tenant_id = s.tenant_id AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE s.configuration ->> 'callback_id' = @EndpointId::text
                  AND s.configuration ->> 'source_contract' = 'github_webhook'
                  AND i.id = @GitHubConnectorId AND i.contract_version = @GitHubContractVersion AND i.key = @ConnectorKey
                  AND s.type = 'webhook' AND s.status = 'active'
                  AND c.status = 'active' AND i.status = 'active'
                LIMIT 1
                """;
        EndpointRow? row = await connection.QuerySingleOrDefaultAsync<EndpointRow>(
            new CommandDefinition(
                sql,
                new
                {
                    EndpointId = endpointId,
                    ConnectorKey = connectorKey,
                    GitHubConnectorId = BuiltinCatalog.GitHubId,
                    BuiltinCatalog.GitHubContractVersion,
                },
                cancellationToken: cancellationToken));

        return row?.ToResolvedSourceEndpoint();
    }

    private sealed record EndpointRow
    {
        public Guid TenantId { get; init; }
        public string TenantSlug { get; init; } = "";
        public Guid TopicId { get; init; }
        public Guid SourceId { get; init; }
        public Guid ConnectionId { get; init; }
        public string ConnectorKey { get; init; } = "";
        public string? SourceVerificationJson { get; init; }

        public ResolvedSourceEndpoint? ToResolvedSourceEndpoint()
        {
            if (SourceVerificationJson is null)
                return null;

            return new ResolvedSourceEndpoint
            {
                TenantId = TenantId,
                TenantSlug = TenantSlug,
                TopicId = TopicId,
                SourceId = SourceId,
                ConnectionId = ConnectionId,
                ConnectorKey = ConnectorKey,
                SourceVerification = JsonSerializer.Deserialize<ConnectionSchemeSelection>(
                    SourceVerificationJson, StoredJson.Options)!,
            };
        }
    }
}
