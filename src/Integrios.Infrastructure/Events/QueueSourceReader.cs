using System.Text.Json;
using Dapper;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace Integrios.Infrastructure.Events;

internal sealed class QueueSourceReader(
    IDbConnectionFactory connectionFactory,
    ILogger<QueueSourceReader> logger) : IQueueSourceReader
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
                    s.event_identity_rule AS EventIdentityRuleJson,
                    s.input_requirements AS SourceContractSchemaJson,
                    s.mapping AS SourceMappingJson,
                    s.revision AS Revision
                FROM sources s
                JOIN connectors i ON i.id = s.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE s.type = N'queue' AND s.status = N'active'
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
                    s.event_identity_rule::text AS EventIdentityRuleJson,
                    s.input_requirements::text AS SourceContractSchemaJson,
                    s.mapping::text AS SourceMappingJson,
                    s.revision AS Revision
                FROM sources s
                JOIN connectors i ON i.id = s.connector_id
                JOIN tenants t ON t.id = s.tenant_id
                WHERE s.type = 'queue' AND s.status = 'active'
                  AND i.status = 'active' AND i.direction IN ('source', 'both')
                  AND s.configuration ->> 'transport' = 'azure_service_bus'
                """;

        IEnumerable<SourceRow> rows = await connection.QueryAsync<SourceRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));

        var resolved = new List<ResolvedQueueSource>();
        foreach (SourceRow row in rows)
        {
            if (row.ToResolvedQueueSource() is { } source)
            {
                resolved.Add(source);
                continue;
            }

            // An active queue Source the receiver cannot address is otherwise invisible: no
            // processor, no error, no metric, and Admin still reports it active.
            logger.LogWarning(
                "Skipping queue Source {SourceId}: its configuration does not resolve to a Service Bus entity.",
                row.SourceId);
        }

        return resolved;
    }

    private sealed record SourceRow
    {
        public Guid TenantId { get; init; }
        public string TenantSlug { get; init; } = "";
        public Guid TopicId { get; init; }
        public Guid SourceId { get; init; }
        public string SourceConfigurationJson { get; init; } = "{}";
        public string? EventIdentityRuleJson { get; init; }
        public string? SourceContractSchemaJson { get; init; }
        public string? SourceMappingJson { get; init; }
        public string Revision { get; init; } = "";


        private static string? ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

        public ResolvedQueueSource? ToResolvedQueueSource()
        {
            JsonElement configuration = JsonSerializer.Deserialize<JsonElement>(SourceConfigurationJson);
            if (!configuration.TryGetProperty("transport_config", out JsonElement transportConfig)
                || transportConfig.ValueKind != JsonValueKind.Object
                || !configuration.TryGetProperty("authentication", out JsonElement authenticationElement))
            {
                return null;
            }

            string? @namespace = ReadString(transportConfig, "namespace");
            if (@namespace is null)
                return null;

            string? queueName = ReadString(transportConfig, "queue_name");
            string? serviceBusTopicName = ReadString(transportConfig, "topic_name");
            string? serviceBusSubscriptionName = ReadString(transportConfig, "subscription_name");
            // Authoring guarantees exactly one form; a row that satisfies neither predates or evades
            // that rule and is skipped rather than started against a half-specified entity.
            if (queueName is null && (serviceBusTopicName is null || serviceBusSubscriptionName is null))
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
                Revision = Revision,
                TenantId = TenantId,
                TenantSlug = TenantSlug,
                TopicId = TopicId,
                SourceId = SourceId,
                Namespace = @namespace,
                QueueName = queueName,
                ServiceBusTopicName = serviceBusTopicName,
                ServiceBusSubscriptionName = serviceBusSubscriptionName,
                Authentication = new QueueAuthentication { Scheme = scheme, SecretReference = secretReference },
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
