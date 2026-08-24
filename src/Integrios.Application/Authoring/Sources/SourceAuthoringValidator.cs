using System.Text.Json;
using Integrios.Application.Authoring.Connections;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Sources;

internal static class SourceAuthoringValidator
{
    public static string Validate(SourceType type, JsonElement configuration, Connection connection, Connector connector)
    {
        try
        {
            ConnectionUseValidator.ValidateSourceAuthoring(connection, connector);
        }
        catch (ConnectionValidationException exception)
        {
            throw new SourceValidationException(exception.Message);
        }

        if (configuration.ValueKind != JsonValueKind.Object)
            throw new SourceValidationException("Source configuration must be a JSON object.");
        if (!configuration.TryGetProperty("source_contract", out JsonElement contract)
            || contract.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(contract.GetString()))
        {
            throw new SourceValidationException("Source configuration requires a source_contract key.");
        }

        string sourceContract = contract.GetString()!;
        if (!connector.Manifest.SourceContracts.Any(candidate => candidate.Key == sourceContract))
            throw new SourceValidationException("Source configuration selects a contract not declared by its Connector.");

        var allowed = type switch
        {
            SourceType.EventApi => new HashSet<string>(["source_contract"], StringComparer.Ordinal),
            SourceType.Webhook => new HashSet<string>(["source_contract", "callback_id"], StringComparer.Ordinal),
            SourceType.Queue => new HashSet<string>(["source_contract", "transport", "namespace", "queue_name", "authentication"], StringComparer.Ordinal),
            _ => throw new SourceValidationException("Source type is not supported."),
        };
        foreach (JsonProperty property in configuration.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new SourceValidationException($"Source configuration property '{property.Name}' is not valid for {type.ToString().ToLowerInvariant()}.");
        }

        if (type == SourceType.Queue)
        {
            if (!configuration.TryGetProperty("transport", out JsonElement transport)
                || transport.ValueKind != JsonValueKind.String
                || transport.GetString() != "azure_service_bus")
            {
                throw new SourceValidationException("Queue Source configuration requires transport azure_service_bus.");
            }
            if (!configuration.TryGetProperty("namespace", out JsonElement @namespace)
                || @namespace.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(@namespace.GetString())
                || !configuration.TryGetProperty("queue_name", out JsonElement queueName)
                || queueName.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(queueName.GetString())
                || !configuration.TryGetProperty("authentication", out JsonElement authentication)
                || authentication.ValueKind != JsonValueKind.Object)
            {
                throw new SourceValidationException("Queue Source configuration requires namespace, queue_name, and authentication.");
            }
        }

        return sourceContract;
    }
}
