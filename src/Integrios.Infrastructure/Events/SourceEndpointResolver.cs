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
                    i.[key] AS ConnectorKey,
                    s.configuration AS SourceConfigurationJson,
                    s.verification AS SourceVerificationJson,
                    s.event_identity_rule AS EventIdentityRuleJson,
                    s.input_requirements AS SourceContractSchemaJson,
                    s.mapping AS SourceMappingJson
                FROM sources s
                JOIN connectors i ON i.id = s.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE JSON_VALUE(s.configuration, '$.callback_id') = @CallbackId
                  AND s.type = N'webhook' AND s.status = N'active'
                  AND i.status = N'active' AND i.direction IN (N'source', N'both')
                """
            : """
                SELECT
                    s.tenant_id AS TenantId,
                    t.slug AS TenantSlug,
                    s.topic_id AS TopicId,
                    s.id AS SourceId,
                    i.key AS ConnectorKey,
                    s.configuration::text AS SourceConfigurationJson,
                    s.verification::text AS SourceVerificationJson,
                    s.event_identity_rule::text AS EventIdentityRuleJson,
                    s.input_requirements::text AS SourceContractSchemaJson,
                    s.mapping::text AS SourceMappingJson
                FROM sources s
                JOIN connectors i ON i.id = s.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE s.configuration ->> 'callback_id' = @CallbackId
                  AND s.type = 'webhook' AND s.status = 'active'
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
        public string ConnectorKey { get; init; } = "";
        public string SourceConfigurationJson { get; init; } = "{}";
        public string? SourceVerificationJson { get; init; }
        public string? EventIdentityRuleJson { get; init; }
        public string? SourceContractSchemaJson { get; init; }
        public string? SourceMappingJson { get; init; }

        public ResolvedSourceEndpoint? ToResolvedSourceEndpoint()
        {
            return new ResolvedSourceEndpoint
            {
                TenantId = TenantId,
                TenantSlug = TenantSlug,
                TopicId = TopicId,
                SourceId = SourceId,
                ConnectorKey = ConnectorKey,
                SourceVerification = SourceVerificationJson is null
                    ? null
                    : JsonSerializer.Deserialize<SourceVerification>(SourceVerificationJson, StoredJson.Options)!,
                EventIdentityRule = string.IsNullOrWhiteSpace(EventIdentityRuleJson)
                    ? null
                    : JsonSerializer.Deserialize<SourceEventIdentityRule>(EventIdentityRuleJson, StoredJson.Options),
                SourceContractSchema = string.IsNullOrWhiteSpace(SourceContractSchemaJson)
                    ? null : JsonSerializer.Deserialize<JsonElement>(SourceContractSchemaJson),
                SourceMapping = string.IsNullOrWhiteSpace(SourceMappingJson)
                    ? null : JsonSerializer.Deserialize<SourceMapping>(SourceMappingJson, StoredJson.Options) is { } mapping
                        ? new TransformSpec(mapping.Engine, mapping.Version, mapping.Expression)
                        : null,
            };
        }
    }
}
