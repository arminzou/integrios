using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Events;

internal sealed class QueueSourceCatalog(IDbConnectionFactory connectionFactory) : IQueueSourceCatalog
{
    public async Task<IReadOnlyList<ResolvedQueueSource>> ListActiveAzureServiceBusSourcesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        string sql = sqlServer
            ? """
                SELECT
                    s.tenant_id AS TenantId, t.slug AS TenantSlug, s.topic_id AS TopicId, s.id AS SourceId,
                    s.configuration AS SourceConfigurationJson,
                    i.manifest AS ManifestJson
                FROM sources s
                JOIN connections c ON c.tenant_id = s.tenant_id AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE s.type = N'queue' AND s.status = N'active'
                  AND c.status = N'active'
                  AND i.status = N'active' AND i.direction IN (N'source', N'both')
                  AND JSON_VALUE(s.configuration, '$.transport') = N'azure_service_bus'
                """
            : """
                SELECT
                    s.tenant_id AS TenantId,
                    t.slug AS TenantSlug,
                    s.topic_id AS TopicId,
                    s.id AS SourceId,
                    s.configuration::text AS SourceConfigurationJson,
                    i.manifest::text AS ManifestJson
                FROM sources s
                JOIN connections c ON c.tenant_id = s.tenant_id AND c.id = s.connection_id
                JOIN connectors i ON i.id = c.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE s.type = 'queue' AND s.status = 'active'
                  AND c.status = 'active'
                  AND i.status = 'active' AND i.direction IN ('source', 'both')
                  AND s.configuration ->> 'transport' = 'azure_service_bus'
                """;

        IEnumerable<SourceRow> rows = await connection.QueryAsync<SourceRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));

        return rows.Select(row => row.ToResolvedQueueSource()).Where(source => source is not null).ToList()!;
    }

    private sealed record SourceRow
    {
        public Guid TenantId { get; init; }
        public string TenantSlug { get; init; } = "";
        public Guid TopicId { get; init; }
        public Guid SourceId { get; init; }
        public string SourceConfigurationJson { get; init; } = "{}";
        public string ManifestJson { get; init; } = "";

        // Both JSON documents this row was built from, hashed together: any edit to the Source
        // configuration or to the Connector manifest it draws its contract from changes the value,
        // and nothing else does. Cheaper and harder to forget than comparing resolved fields.
        private string RevisionOf() => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{SourceConfigurationJson}{ManifestJson}")));


        public ResolvedQueueSource? ToResolvedQueueSource()
        {
            JsonElement configuration = JsonSerializer.Deserialize<JsonElement>(SourceConfigurationJson);
            if (!configuration.TryGetProperty("source_contract", out JsonElement contractKeyElement)
                || !configuration.TryGetProperty("namespace", out JsonElement namespaceElement)
                || !configuration.TryGetProperty("queue_name", out JsonElement queueNameElement)
                || !configuration.TryGetProperty("authentication", out JsonElement authenticationElement))
            {
                return null;
            }

            ConnectorManifest manifest = JsonSerializer.Deserialize<ConnectorManifest>(
                ManifestJson, StoredJson.Options)!;
            ConnectorSourceContractManifest? contract = manifest.SourceContracts
                .FirstOrDefault(candidate => candidate.Key == contractKeyElement.GetString());
            if (contract is null)
                return null;

            string? scheme = authenticationElement.TryGetProperty("scheme", out JsonElement schemeElement)
                ? schemeElement.GetString()
                : null;
            if (scheme is null)
                return null;
            string? secretReference = authenticationElement.TryGetProperty("secret_ref", out JsonElement secretRefElement)
                ? secretRefElement.GetString()
                : null;

            return new ResolvedQueueSource
            {
                Revision = RevisionOf(),
                TenantId = TenantId,
                TenantSlug = TenantSlug,
                TopicId = TopicId,
                SourceId = SourceId,
                Namespace = namespaceElement.GetString() ?? "",
                QueueName = queueNameElement.GetString() ?? "",
                Authentication = new QueueAuthentication { Scheme = scheme, SecretReference = secretReference },
                SourceContractSchema = contract.Schema,
                SourceMapping = contract.Mapping is { } mapping
                    ? new TransformSpec(mapping.Engine, mapping.Version, mapping.Expression)
                    : null,
            };
        }
    }
}
