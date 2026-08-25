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
            // Fixed regardless of how many transports exist: everything transport-specific lives
            // inside transport_config, so adding a broker never widens this set.
            SourceType.Queue => new HashSet<string>(
                ["source_contract", "transport", "authentication", "transport_config"],
                StringComparer.Ordinal),
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
            if (!configuration.TryGetProperty("transport_config", out JsonElement transportConfig)
                || transportConfig.ValueKind != JsonValueKind.Object
                || !configuration.TryGetProperty("authentication", out JsonElement authentication)
                || authentication.ValueKind != JsonValueKind.Object)
            {
                throw new SourceValidationException(
                    "Queue Source configuration requires transport_config and authentication objects.");
            }

            string @namespace = ValidateServiceBusTransportConfig(transportConfig);
            ValidateQueueAuthentication(authentication, @namespace);
        }

        return sourceContract;
    }

    // Everything Service Bus needs to reach one entity. Nested rather than prefixed: transport
    // already discriminates, and inside this object topic_name and subscription_name cannot be
    // misread as the Integrios Topic the Source publishes to or the Subscriptions that consume it.
    private static string ValidateServiceBusTransportConfig(JsonElement transportConfig)
    {
        var allowed = new HashSet<string>(
            ["namespace", "queue_name", "topic_name", "subscription_name"], StringComparer.Ordinal);
        foreach (JsonProperty property in transportConfig.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new SourceValidationException(
                    $"Queue Source transport_config property '{property.Name}' is not valid for azure_service_bus.");
            }
        }

        if (!IsNonEmptyString(transportConfig, "namespace"))
            throw new SourceValidationException("Queue Source transport_config requires a namespace.");

        bool hasQueue = IsNonEmptyString(transportConfig, "queue_name");
        bool hasTopic = IsNonEmptyString(transportConfig, "topic_name");
        bool hasSubscription = IsNonEmptyString(transportConfig, "subscription_name");

        if (hasQueue && (hasTopic || hasSubscription))
        {
            throw new SourceValidationException(
                "Queue Source transport_config names either queue_name or a topic subscription, not both.");
        }
        if (hasTopic != hasSubscription)
        {
            throw new SourceValidationException(
                "Queue Source transport_config requires topic_name and subscription_name together.");
        }
        if (!hasQueue && !hasTopic)
        {
            throw new SourceValidationException(
                "Queue Source transport_config requires queue_name, or topic_name with subscription_name.");
        }

        return transportConfig.GetProperty("namespace").GetString()!;
    }

    private static bool IsNonEmptyString(JsonElement configuration, string property) =>
        configuration.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    // Authentication the receiver cannot build a client for is rejected here, because nothing
    // downstream reports it: a broker that refuses the credential leaves a processor that looks
    // healthy to reconciliation while receiving nothing. These are the schemes
    // AzureServiceBusQueueReceiver.CreateClientAsync implements; the two lists move together.
    private static void ValidateQueueAuthentication(JsonElement authentication, string @namespace)
    {
        string? scheme = authentication.TryGetProperty("scheme", out JsonElement schemeElement)
            && schemeElement.ValueKind == JsonValueKind.String
                ? schemeElement.GetString()
                : null;
        bool hasSecretReference = authentication.TryGetProperty("secret_ref", out JsonElement secretRef)
            && secretRef.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(secretRef.GetString());

        switch (scheme)
        {
            case "connection_string" when !hasSecretReference:
                throw new SourceValidationException(
                    "Queue Source connection_string authentication requires a secret_ref.");
            case "azure_identity" when hasSecretReference:
                throw new SourceValidationException(
                    "Queue Source azure_identity authentication draws an ambient credential and takes no secret_ref.");
            case "azure_identity" when !IsHostName(@namespace):
                throw new SourceValidationException(
                    "Queue Source azure_identity authentication requires namespace to be the broker host "
                    + $"name, such as 'example.servicebus.windows.net'; got '{@namespace}'.");
            case "connection_string" or "azure_identity":
                return;
            default:
                throw new SourceValidationException(
                    $"Queue Source authentication scheme '{scheme}' is not supported; use connection_string or azure_identity.");
        }
    }

    // azure_identity locates the broker from this value alone, so it must be a bare host: no
    // scheme, no path, and at least one dot. Deliberately not pinned to servicebus.windows.net,
    // because sovereign clouds use different suffixes.
    private static bool IsHostName(string value) =>
        !value.Contains("://", StringComparison.Ordinal)
        && !value.Contains('/')
        && value.Contains('.')
        && Uri.CheckHostName(value) == UriHostNameType.Dns;
}
