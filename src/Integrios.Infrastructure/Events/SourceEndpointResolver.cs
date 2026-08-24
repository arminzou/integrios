using System.Text.Json;
using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class SourceEndpointResolver(IDbConnectionFactory connectionFactory)
    : ISourceEndpointResolver
{
    public async Task<ResolvedSourceEndpoint?> ResolveAsync(
        Guid callbackId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string sql = sqlServer
            ? """
                SELECT TOP (1)
                    s.tenant_id AS TenantId, t.slug AS TenantSlug, s.topic_id AS TopicId, s.id AS SourceId,
                    s.connection_id AS ConnectionId, i.[key] AS ConnectorKey,
                    s.configuration AS SourceConfigurationJson,
                    c.source_verification AS SourceVerificationJson,
                    i.manifest AS ManifestJson
                FROM sources s
                JOIN connections c ON c.tenant_id = s.tenant_id AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE JSON_VALUE(s.configuration, '$.callback_id') = @CallbackId
                  AND s.type = N'webhook' AND s.status = N'active'
                  AND c.status = N'active'
                  AND i.status = N'active' AND i.direction IN (N'source', N'both')
                """
            : """
                SELECT
                    s.tenant_id AS TenantId,
                    t.slug AS TenantSlug,
                    s.topic_id AS TopicId,
                    s.id AS SourceId,
                    s.connection_id AS ConnectionId,
                    i.key AS ConnectorKey,
                    s.configuration::text AS SourceConfigurationJson,
                    c.source_verification::text AS SourceVerificationJson,
                    i.manifest::text AS ManifestJson
                FROM sources s
                JOIN connections c ON c.tenant_id = s.tenant_id AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE s.configuration ->> 'callback_id' = @CallbackId
                  AND s.type = 'webhook' AND s.status = 'active'
                  AND c.status = 'active'
                  AND i.status = 'active' AND i.direction IN ('source', 'both')
                LIMIT 1
                """;
        EndpointRow? row = await connection.QuerySingleOrDefaultAsync<EndpointRow>(
            new CommandDefinition(
                sql,
                new { CallbackId = callbackId.ToString() },
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
        public string SourceConfigurationJson { get; init; } = "{}";
        public string? SourceVerificationJson { get; init; }
        public string ManifestJson { get; init; } = "";

        public ResolvedSourceEndpoint? ToResolvedSourceEndpoint()
        {
            JsonElement sourceConfiguration = JsonSerializer.Deserialize<JsonElement>(SourceConfigurationJson);
            if (!sourceConfiguration.TryGetProperty("source_contract", out JsonElement contractKeyElement))
                return null;
            string? contractKey = contractKeyElement.GetString();

            ConnectorManifest manifest = JsonSerializer.Deserialize<ConnectorManifest>(
                ManifestJson, StoredJson.Options)!;
            ConnectorSourceContractManifest? contract = manifest.SourceContracts
                .FirstOrDefault(candidate => candidate.Key == contractKey);
            if (contract is null)
                return null;

            return new ResolvedSourceEndpoint
            {
                TenantId = TenantId,
                TenantSlug = TenantSlug,
                TopicId = TopicId,
                SourceId = SourceId,
                ConnectionId = ConnectionId,
                ConnectorKey = ConnectorKey,
                SourceVerification = SourceVerificationJson is null
                    ? null
                    : JsonSerializer.Deserialize<SourceVerification>(SourceVerificationJson, StoredJson.Options)!,
                SourceContractSchema = contract.Schema,
                SourceMapping = contract.Mapping is { } mapping
                    ? new TransformSpec(mapping.Engine, mapping.Version, mapping.Expression)
                    : null,
            };
        }
    }
}
