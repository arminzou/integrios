using System.Text.Json;
using System.Text.RegularExpressions;
using Integrios.Application.Auth;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Integrations;

public static partial class IntegrationManifestParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> TopLevelProperties =
    [
        "manifest_schema_version",
        "key",
        "contract_version",
        "direction",
        "source_configuration_schema",
        "destination_configuration_schema",
        "source_verification",
        "destination_authentication",
        "built_in_source_adapter",
        "http_outcome",
        "presentation",
    ];

    public static IntegrationManifest Parse(
        JsonElement document,
        IAuthSchemeRegistry authenticationSchemes,
        IntegrationManifestApplyAuthority authority)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw Invalid("The Integration manifest must be a JSON object.");

        RejectUnknownProperties(document, TopLevelProperties, "Integration manifest");

        IntegrationManifest manifest;
        try
        {
            manifest = document.Deserialize<IntegrationManifest>(SerializerOptions)
                ?? throw Invalid("The Integration manifest body is required.");
        }
        catch (JsonException exception)
        {
            throw Invalid($"The Integration manifest is invalid: {exception.Message}");
        }

        Validate(manifest, document, authenticationSchemes, authority);
        return Canonicalize(manifest);
    }

    public static IntegrationManifest DeserializeStored(string json) =>
        JsonSerializer.Deserialize<IntegrationManifest>(json, SerializerOptions)
        ?? throw new InvalidOperationException("The stored Integration manifest is required.");

    public static JsonElement ToJson(IntegrationManifest manifest) =>
        JsonSerializer.SerializeToElement(manifest, SerializerOptions);

    public static JsonElement ToPresentationJson(IntegrationPresentationManifest presentation) =>
        JsonSerializer.SerializeToElement(presentation, SerializerOptions);

    public static JsonElement ToFunctionalJson(IntegrationManifest manifest)
    {
        JsonElement complete = ToJson(Canonicalize(manifest));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in complete.EnumerateObject())
            {
                if (property.NameEquals("presentation"))
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray()).Clone();
    }

    private static void Validate(
        IntegrationManifest manifest,
        JsonElement document,
        IAuthSchemeRegistry authenticationSchemes,
        IntegrationManifestApplyAuthority authority)
    {
        if (manifest.ManifestSchemaVersion != 1)
            throw Invalid("manifest_schema_version must be 1.");
        if (string.IsNullOrWhiteSpace(manifest.Key) || !IntegrationKeyPattern().IsMatch(manifest.Key))
            throw Invalid("key must use lower snake_case and start with a letter.");
        if (manifest.ContractVersion < 1)
            throw Invalid("contract_version must be a positive integer.");
        if (!document.TryGetProperty("source_verification", out JsonElement sourceVerificationDocument)
            || !document.TryGetProperty("destination_authentication", out JsonElement destinationAuthenticationDocument))
        {
            throw Invalid("source_verification and destination_authentication are required.");
        }
        ValidatePermissionDocument(
            sourceVerificationDocument, "source_verification", "allow_unverified", out JsonElement sourceSchemes);
        ValidatePermissionDocument(
            destinationAuthenticationDocument, "destination_authentication", "allow_unauthenticated", out JsonElement destinationSchemes);
        ValidateSchemeDocuments(sourceSchemes, "source_verification.schemes");
        ValidateSchemeDocuments(destinationSchemes, "destination_authentication.schemes");

        IntegrationDirection direction = manifest.Direction switch
        {
            "source" => IntegrationDirection.Source,
            "destination" => IntegrationDirection.Destination,
            "both" => IntegrationDirection.Both,
            _ => throw Invalid("direction must be source, destination, or both."),
        };

        bool sourceCapable = direction is IntegrationDirection.Source or IntegrationDirection.Both;
        bool destinationCapable = direction is IntegrationDirection.Destination or IntegrationDirection.Both;

        ValidateDirectionalSchema(document, "source_configuration_schema", sourceCapable);
        ValidateDirectionalSchema(document, "destination_configuration_schema", destinationCapable);

        ValidateSchemes(manifest.SourceVerification.Schemes, "source_verification.schemes");
        ValidateSchemes(manifest.DestinationAuthentication.Schemes, "destination_authentication.schemes");
        ValidatePlatformSchemes(manifest, authenticationSchemes, authority);
        if (!sourceCapable && manifest.SourceVerification.Schemes.Count > 0)
            throw Invalid("source_verification.schemes requires a source-capable direction.");
        if (!destinationCapable && manifest.DestinationAuthentication.Schemes.Count > 0)
            throw Invalid("destination_authentication.schemes requires a destination-capable direction.");
        if (sourceCapable
            && manifest.SourceVerification.Schemes.Count == 0
            && !manifest.SourceVerification.AllowUnverified)
        {
            throw Invalid("source_verification must declare a scheme or set allow_unverified to true.");
        }
        if (destinationCapable
            && manifest.DestinationAuthentication.Schemes.Count == 0
            && !manifest.DestinationAuthentication.AllowUnauthenticated)
        {
            throw Invalid("destination_authentication must declare a scheme or set allow_unauthenticated to true.");
        }

        if (manifest.BuiltInSourceAdapter is not null)
        {
            if (authority.Mode != IntegrationManifestApplyMode.Bootstrap)
                throw Invalid("built_in_source_adapter may be selected only by Bootstrap.");
            if (!sourceCapable || manifest.BuiltInSourceAdapter != "github")
                throw Invalid("built_in_source_adapter requires a source-capable direction and a lower snake_case value.");
        }

        if (manifest.HttpOutcome is JsonElement outcome)
        {
            if (!destinationCapable)
                throw Invalid("http_outcome requires a destination-capable direction.");
            ValidateHttpOutcome(outcome);
        }

        if (!document.TryGetProperty("presentation", out JsonElement presentationDocument)
            || presentationDocument.ValueKind != JsonValueKind.Object
            || manifest.Presentation is null)
        {
            throw Invalid("presentation is required and must be an object.");
        }
        ValidatePresentation(manifest.Presentation, presentationDocument);
    }

    private static void ValidatePlatformSchemes(
        IntegrationManifest manifest,
        IAuthSchemeRegistry authenticationSchemes,
        IntegrationManifestApplyAuthority authority)
    {
        if (authority.Mode != IntegrationManifestApplyMode.Bootstrap
            && manifest.SourceVerification.Schemes.Count > 0)
        {
            throw Invalid(
                "Operator-authored manifests cannot declare source verification schemes without a compiled built-in source adapter.");
        }

        foreach (IntegrationSchemeManifest scheme in manifest.SourceVerification.Schemes)
        {
            if (scheme.Scheme != "github_hmac_sha256"
                || scheme.RequiredConfig.Count != 0
                || !SetEquals(scheme.RequiredSecretRefs, ["webhook_secret"]))
            {
                throw Invalid($"Source verification scheme '{scheme.Scheme}' is not a supported platform contract.");
            }
        }

        foreach (IntegrationSchemeManifest scheme in manifest.DestinationAuthentication.Schemes)
        {
            if (!authenticationSchemes.TryGet(scheme.Scheme, out IAuthSchemeHandler handler)
                || !SetEquals(scheme.RequiredConfig, handler.RequiredConfigFields)
                || !SetEquals(scheme.RequiredSecretRefs, handler.RequiredSecretFields))
            {
                throw Invalid($"Destination authentication scheme '{scheme.Scheme}' is not a supported platform contract.");
            }
        }
    }

    private static bool SetEquals(IEnumerable<string> first, IEnumerable<string> second) =>
        first.Order(StringComparer.Ordinal).SequenceEqual(second.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static void ValidateDirectionalSchema(
        JsonElement document,
        string propertyName,
        bool capable)
    {
        bool present = document.TryGetProperty(propertyName, out JsonElement schema);
        if (capable && !present)
            throw Invalid($"{propertyName} is required for this direction.");
        if (!capable && present)
            throw Invalid($"{propertyName} is not allowed for this direction.");
        if (capable)
            ConstrainedJsonSchemaValidator.Validate(schema, propertyName);
    }

    private static void ValidateSchemes(IReadOnlyList<IntegrationSchemeManifest> schemes, string fieldName)
    {
        if (schemes is null)
            throw Invalid($"{fieldName} must be an array.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (IntegrationSchemeManifest scheme in schemes)
        {
            if (string.IsNullOrWhiteSpace(scheme.Scheme) || !IntegrationKeyPattern().IsMatch(scheme.Scheme))
                throw Invalid($"{fieldName} scheme names must use lower snake_case.");
            if (!names.Add(scheme.Scheme))
                throw Invalid($"{fieldName} contains duplicate scheme '{scheme.Scheme}'.");
            ValidateFieldNames(scheme.RequiredConfig, $"{fieldName}.{scheme.Scheme}.required_config");
            ValidateFieldNames(scheme.RequiredSecretRefs, $"{fieldName}.{scheme.Scheme}.required_secret_refs");
        }
    }

    private static void ValidatePermissionDocument(
        JsonElement document,
        string fieldName,
        string permissionProperty,
        out JsonElement schemes)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw Invalid($"{fieldName} must be an object.");
        RejectUnknownProperties(document, new HashSet<string>([permissionProperty, "schemes"]), fieldName);
        if (!document.TryGetProperty(permissionProperty, out JsonElement permission)
            || permission.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid($"{fieldName}.{permissionProperty} is required and must be a boolean.");
        }
        if (!document.TryGetProperty("schemes", out schemes) || schemes.ValueKind != JsonValueKind.Array)
            throw Invalid($"{fieldName}.schemes is required and must be an array.");
    }

    private static void ValidateSchemeDocuments(JsonElement schemes, string fieldName)
    {
        if (schemes.ValueKind != JsonValueKind.Array)
            throw Invalid($"{fieldName} must be an array.");
        foreach (JsonElement scheme in schemes.EnumerateArray())
        {
            if (scheme.ValueKind != JsonValueKind.Object)
                throw Invalid($"{fieldName} must contain only objects.");
            RejectUnknownProperties(
                scheme,
                new HashSet<string>(["scheme", "required_config", "required_secret_refs"]),
                fieldName);
            if (!scheme.TryGetProperty("scheme", out JsonElement schemeName)
                || schemeName.ValueKind != JsonValueKind.String
                || !scheme.TryGetProperty("required_config", out JsonElement requiredConfig)
                || requiredConfig.ValueKind != JsonValueKind.Array
                || !scheme.TryGetProperty("required_secret_refs", out JsonElement requiredSecretRefs)
                || requiredSecretRefs.ValueKind != JsonValueKind.Array)
            {
                throw Invalid(
                    $"{fieldName} entries require scheme, required_config, and required_secret_refs.");
            }
        }
    }

    private static void ValidateFieldNames(IReadOnlyList<string> fields, string path)
    {
        if (fields is null)
            throw Invalid($"{path} must be an array.");
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string field in fields)
        {
            if (!IntegrationKeyPattern().IsMatch(field) || !unique.Add(field))
                throw Invalid($"{path} must contain unique lower snake_case field names.");
        }
    }

    private static void ValidateHttpOutcome(JsonElement outcome)
    {
        if (outcome.ValueKind != JsonValueKind.Object)
            throw Invalid("http_outcome must be an object.");
        if (!outcome.TryGetProperty("evaluator", out JsonElement evaluatorElement)
            || evaluatorElement.ValueKind != JsonValueKind.String)
        {
            throw Invalid("http_outcome.evaluator is required.");
        }

        string evaluator = evaluatorElement.GetString()!;
        HashSet<string> allowed = evaluator switch
        {
            "status_code" => ["evaluator"],
            "json_boolean" => ["evaluator", "field", "expected", "diagnostic_field", "max_body_bytes"],
            _ => throw Invalid("http_outcome.evaluator must be status_code or json_boolean."),
        };
        RejectUnknownProperties(outcome, allowed, "http_outcome");

        if (evaluator == "json_boolean")
        {
            if (!outcome.TryGetProperty("field", out JsonElement field)
                || field.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(field.GetString()))
            {
                throw Invalid("http_outcome.field is required for json_boolean.");
            }
            if (!outcome.TryGetProperty("expected", out JsonElement expected)
                || expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Invalid("http_outcome.expected must be a boolean for json_boolean.");
            }
            if (outcome.TryGetProperty("diagnostic_field", out JsonElement diagnosticField)
                && (diagnosticField.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(diagnosticField.GetString())))
            {
                throw Invalid("http_outcome.diagnostic_field must be a non-empty top-level field name.");
            }
            if (outcome.TryGetProperty("max_body_bytes", out JsonElement maxBytes)
                && (maxBytes.ValueKind != JsonValueKind.Number
                    || !maxBytes.TryGetInt32(out int value)
                    || value is < 1 or > 1_048_576))
            {
                throw Invalid("http_outcome.max_body_bytes must be an integer from 1 through 1048576.");
            }
        }
    }

    private static void ValidatePresentation(IntegrationPresentationManifest presentation, JsonElement document)
    {
        RejectUnknownProperties(
            document,
            new HashSet<string>(["name", "description", "event_types", "authoring_presets"]),
            "presentation");
        if (!document.TryGetProperty("event_types", out JsonElement eventTypes)
            || eventTypes.ValueKind != JsonValueKind.Array
            || !document.TryGetProperty("authoring_presets", out JsonElement authoringPresets)
            || authoringPresets.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("presentation.event_types and presentation.authoring_presets are required arrays.");
        }
        if (string.IsNullOrWhiteSpace(presentation.Name))
            throw Invalid("presentation.name is required.");
        if (presentation.EventTypes is null || presentation.AuthoringPresets is null)
            throw Invalid("presentation.event_types and presentation.authoring_presets must be arrays.");
        if (presentation.EventTypes.Any(string.IsNullOrWhiteSpace))
            throw Invalid("presentation.event_types cannot contain empty values.");
        if (presentation.EventTypes.Count != presentation.EventTypes.Distinct(StringComparer.Ordinal).Count())
            throw Invalid("presentation.event_types cannot contain duplicates.");
        if (presentation.AuthoringPresets.Any(preset => preset.ValueKind != JsonValueKind.Object))
            throw Invalid("presentation.authoring_presets must contain only objects.");
    }

    private static IntegrationManifest Canonicalize(IntegrationManifest manifest) => manifest with
    {
        SourceConfigurationSchema = manifest.SourceConfigurationSchema is JsonElement sourceSchema
            ? CanonicalizeSchema(sourceSchema)
            : null,
        DestinationConfigurationSchema = manifest.DestinationConfigurationSchema is JsonElement destinationSchema
            ? CanonicalizeSchema(destinationSchema)
            : null,
        SourceVerification = manifest.SourceVerification with
        {
            Schemes = CanonicalizeSchemes(manifest.SourceVerification.Schemes),
        },
        DestinationAuthentication = manifest.DestinationAuthentication with
        {
            Schemes = CanonicalizeSchemes(manifest.DestinationAuthentication.Schemes),
        },
        Presentation = manifest.Presentation with
        {
            EventTypes = manifest.Presentation.EventTypes.ToArray(),
            AuthoringPresets = manifest.Presentation.AuthoringPresets.Select(preset => preset.Clone()).ToArray(),
        },
        HttpOutcome = manifest.HttpOutcome?.Clone(),
    };

    private static IReadOnlyList<IntegrationSchemeManifest> CanonicalizeSchemes(
        IReadOnlyList<IntegrationSchemeManifest> schemes) => schemes
        .OrderBy(scheme => scheme.Scheme, StringComparer.Ordinal)
        .Select(scheme => scheme with
        {
            RequiredConfig = scheme.RequiredConfig.Order(StringComparer.Ordinal).ToArray(),
            RequiredSecretRefs = scheme.RequiredSecretRefs.Order(StringComparer.Ordinal).ToArray(),
        })
        .ToArray();

    private static JsonElement CanonicalizeSchema(JsonElement schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in schema.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                if (property.Name is "required" or "enum")
                {
                    writer.WriteStartArray();
                    foreach (JsonElement item in property.Value.EnumerateArray()
                                 .OrderBy(item => item.GetRawText(), StringComparer.Ordinal))
                    {
                        item.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else if (property.Name == "properties")
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty child in property.Value.EnumerateObject()
                                 .OrderBy(child => child.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(child.Name);
                        CanonicalizeSchema(child.Value).WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray()).Clone();
    }

    private static void RejectUnknownProperties(JsonElement value, IReadOnlySet<string> allowed, string path)
    {
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw Invalid($"{path} contains unsupported property '{property.Name}'.");
        }
    }

    private static IntegrationManifestValidationException Invalid(string message) => new(message);

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegrationKeyPattern();
}
