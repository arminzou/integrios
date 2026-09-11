using System.Text.Json;
using Integrios.Application.Common;
using Integrios.Application.Secrets;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Sources;

internal static class SourceAuthoringValidator
{
    public static void Validate(
        SourceType type,
        JsonElement configuration,
        SourceVerificationInput? verification,
        Connector connector)
    {
        if (connector.Direction == ConnectorDirection.Destination)
            throw new SourceValidationException($"The Connector '{connector.Key}' does not permit Source authoring.");
        if (connector.Status != OperationalStatus.Active)
            throw new SourceValidationException("The Source's Connector must be active before it can be used.");
        if (connector.Manifest.SourceConfigurationSchema is not JsonElement schema)
            throw new SourceValidationException("The Connector does not declare a Source configuration schema.");

        try
        {
            ConfigurationSchemaEvaluator.Validate(configuration, schema, "Source configuration");
        }
        catch (ConfigurationValidationException exception)
        {
            throw new SourceValidationException(exception.Message);
        }

        if (type == SourceType.Webhook)
        {
            ValidateVerification(verification, connector.Manifest.SourceVerification);
            return;
        }

        if (type is SourceType.EventApi or SourceType.Queue)
        {
            if (verification is not null)
                throw new SourceValidationException("Only webhook Sources support Source verification.");
            if (type == SourceType.Queue)
                ValidateQueueConfiguration(configuration);
            return;
        }

        throw new SourceValidationException("Source type is not supported.");
    }

    public static SourceVerification? ToVerification(SourceVerificationInput? input) => input is null
        ? null
        : new SourceVerification
        {
            Scheme = input.Scheme,
            Config = input.Config.Clone(),
            SecretRefs = input.SecretRefs.Clone(),
        };

    public static void ValidateRuntimeContract(
        SourceType type,
        JsonElement? inputRequirements,
        SourceMapping? mapping,
        SourceEventIdentityRule? eventIdentityRule,
        ITransformEvaluator evaluator)
    {
        if (type == SourceType.EventApi)
        {
            if (inputRequirements is not null || mapping is not null || eventIdentityRule is not null)
                throw new SourceValidationException("Event API Sources use the fixed Integrios Event contract.");
            return;
        }

        if (inputRequirements is JsonElement schema)
        {
            try
            {
                ConstrainedJsonSchemaValidator.Validate(schema, "input_requirements");
            }
            catch (ConnectorManifestValidationException exception)
            {
                throw new SourceValidationException(exception.Message, "input_requirements");
            }
        }

        if (mapping is not null && evaluator.ValidateExpression(new TransformSpec(mapping.Engine, mapping.Version, mapping.Expression)) is { } error)
            throw new SourceValidationException(error, "mapping");

        if (eventIdentityRule is null)
            return;
        if (string.IsNullOrWhiteSpace(eventIdentityRule.Value))
            throw new SourceValidationException("Source Event identity value is required.", "event_identity_rule");

        bool supported = type switch
        {
            SourceType.Webhook => eventIdentityRule.Kind is "header" or "json_path",
            SourceType.Queue => eventIdentityRule.Kind is "message_id" or "json_path",
            _ => false,
        };
        if (!supported)
            throw new SourceValidationException("Source Event identity rule is not valid for this Source type.", "event_identity_rule");
        if (eventIdentityRule.Kind == "json_path" && !SourceEventIdentityExtractor.IsJsonPointer(eventIdentityRule.Value))
            throw new SourceValidationException("Source Event JSON identity path must be a JSON Pointer such as '/id'.", "event_identity_rule");
    }

    private static void ValidateVerification(
        SourceVerificationInput? verification,
        ConnectorSourceVerificationManifest capabilities)
    {
        if (verification is null)
        {
            if (!capabilities.AllowUnverified)
                throw new SourceValidationException("The Source requires a Source verification selection before it can be active.");
            return;
        }

        ConnectorSchemeManifest? declaration = capabilities.Schemes.SingleOrDefault(
            scheme => scheme.Scheme.Equals(verification.Scheme, StringComparison.OrdinalIgnoreCase));
        if (declaration is null)
            throw new SourceValidationException($"Source verification scheme '{verification.Scheme}' is not supported by this Connector.");

        ValidateRequiredFields(verification.Config, declaration.RequiredConfig, "config");
        ValidateRequiredFields(verification.SecretRefs, declaration.RequiredSecretRefs, "secret_refs");
        if (SecretReferenceMap.Validate(verification.SecretRefs, "Source verification secret_refs") is { } error)
            throw new SourceValidationException(error, "verification");
    }

    private static void ValidateRequiredFields(JsonElement value, IReadOnlyList<string> required, string section)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new SourceValidationException($"Source verification {section} must be a JSON object.");
        foreach (string field in required)
        {
            if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                throw new SourceValidationException($"Source verification {section} field '{field}' is required.");
        }
    }

    private static void ValidateQueueConfiguration(JsonElement configuration)
    {
        if (!configuration.TryGetProperty("transport", out JsonElement transport)
            || transport.ValueKind != JsonValueKind.String
            || transport.GetString() != "azure_service_bus"
            || !configuration.TryGetProperty("transport_config", out JsonElement transportConfig)
            || transportConfig.ValueKind != JsonValueKind.Object
            || !configuration.TryGetProperty("authentication", out JsonElement authentication)
            || authentication.ValueKind != JsonValueKind.Object)
        {
            throw new SourceValidationException(
                "Queue Source configuration requires azure_service_bus transport_config and authentication objects.");
        }

        string? @namespace = ReadNonEmptyString(transportConfig, "namespace");
        if (@namespace is null)
            throw new SourceValidationException("Queue Source transport_config requires a namespace.");
        bool hasQueue = ReadNonEmptyString(transportConfig, "queue_name") is not null;
        bool hasTopic = ReadNonEmptyString(transportConfig, "topic_name") is not null;
        bool hasSubscription = ReadNonEmptyString(transportConfig, "subscription_name") is not null;
        if (hasQueue == (hasTopic || hasSubscription) || hasTopic != hasSubscription)
            throw new SourceValidationException("Queue Source transport_config requires exactly one queue or topic subscription.");

        string? scheme = ReadNonEmptyString(authentication, "scheme");
        bool hasSecretReference = ReadNonEmptyString(authentication, "secret_ref") is not null;
        if (scheme == "connection_string" && !hasSecretReference)
            throw new SourceValidationException("Queue Source connection_string authentication requires a secret_ref.");
        if (hasSecretReference && !SecretReferenceName.IsValid(ReadNonEmptyString(authentication, "secret_ref")))
        {
            throw new SourceValidationException(
                "Queue Source secret_ref must be a lowercase logical name of 1 to 63 characters. "
                + "It names a secret; it is never the secret itself.",
                "configuration");
        }
        if (scheme == "azure_identity" && hasSecretReference)
            throw new SourceValidationException("Queue Source azure_identity authentication takes no secret_ref.");
        if (scheme == "azure_identity" && !IsHostName(@namespace))
            throw new SourceValidationException("Queue Source azure_identity authentication requires a broker host namespace.");
        if (scheme is not ("connection_string" or "azure_identity"))
            throw new SourceValidationException("Queue Source authentication supports connection_string or azure_identity.");
    }

    private static string? ReadNonEmptyString(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement item)
        && item.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString() : null;

    private static bool IsHostName(string value) =>
        !value.Contains("://", StringComparison.Ordinal)
        && !value.Contains('/')
        && value.Contains('.')
        && Uri.CheckHostName(value) == UriHostNameType.Dns;
}
