using System.Text.Json;
using System.Text.Json.Nodes;
using Integrios.Application.Auth;
using Integrios.Application.Bootstrap;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;

namespace Integrios.Admin.Tests;

public sealed class IntegrationManifestParserTests
{
    [Fact]
    public void Parse_AcceptsTheConstrainedScalarSchemaSubset()
    {
        var manifest = Parse(Json("""
            {
              "manifest_schema_version": 1,
              "key": "example_api",
              "contract_version": 2,
              "direction": "destination",
              "destination_configuration_schema": {
                "type": "object",
                "properties": {
                  "base_uri": { "type": "string", "format": "uri", "minLength": 8, "maxLength": 200 },
                  "port": { "type": "integer", "minimum": 1, "maximum": 65535 },
                  "enabled": { "type": "boolean", "enum": [true] }
                },
                "required": ["base_uri"],
                "additionalProperties": false
              },
              "source_verification": { "allow_unverified": true, "schemes": [] },
              "destination_authentication": {
                "allow_unauthenticated": false,
                "schemes": [
                  { "scheme": "bearer_token", "required_config": [], "required_secret_refs": ["token"] }
                ]
              },
              "presentation": { "name": "Example API", "event_types": [], "authoring_presets": [] }
            }
            """));

        Assert.Equal("example_api", manifest.Key);
        Assert.Equal(2, manifest.ContractVersion);
        Assert.Equal("bearer_token", Assert.Single(manifest.DestinationAuthentication.Schemes).Scheme);
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("$ref")]
    [InlineData("oneOf")]
    [InlineData("default")]
    public void Parse_RejectsUnsupportedJsonSchemaKeywords(string keyword)
    {
        string json = ValidManifest().Replace(
            "\"type\": \"string\"",
            $"\"type\": \"string\", \"{keyword}\": \"unsupported\"",
            StringComparison.Ordinal);

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("unsupported JSON Schema keyword", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsNestedObjectSchemas()
    {
        string json = ValidManifest().Replace(
            "\"type\": \"string\"",
            "\"type\": \"object\", \"properties\": {}",
            StringComparison.Ordinal);

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("nested object schemas", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnknownTopLevelProperty()
    {
        string json = ValidManifest().Replace(
            "\"manifest_schema_version\": 1,",
            "\"manifest_schema_version\": 1, \"extension\": true,",
            StringComparison.Ordinal);

        Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));
    }

    [Theory]
    [InlineData("Bad-Key", 1, "destination")]
    [InlineData("valid_key", 0, "destination")]
    [InlineData("valid_key", 1, "sideways")]
    [InlineData("valid_key", 1, "1")]
    public void Parse_RejectsInvalidIdentityOrDirection(string key, int version, string direction)
    {
        string json = ValidManifest()
            .Replace("example_api", key, StringComparison.Ordinal)
            .Replace("\"contract_version\": 1", $"\"contract_version\": {version}", StringComparison.Ordinal)
            .Replace("\"direction\": \"destination\"", $"\"direction\": \"{direction}\"", StringComparison.Ordinal);

        Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));
    }

    [Fact]
    public void Parse_RejectsUnknownOrMisdeclaredPlatformScheme()
    {
        string json = ValidManifest().Replace(
            "\"destination_authentication\": { \"allow_unauthenticated\": true, \"schemes\": [] }",
            "\"destination_authentication\": { \"allow_unauthenticated\": true, \"schemes\": [{\"scheme\":\"bearer_token\",\"required_config\":[],\"required_secret_refs\":[]}] }",
            StringComparison.Ordinal);

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("not a supported platform contract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSourceAdapterSelectionForOperatorWhenNotAuthoringSafe()
    {
        string json = ManifestWithSourceAdapter("github_native", schemeName: "hmac_sha256");

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("only by Bootstrap", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsOperatorAuthoringSafeSourceAdapterSelection()
    {
        var manifest = Parse(Json(ManifestWithSourceAdapter("verified_webhook", schemeName: "hmac_sha256")));

        Assert.Equal("verified_webhook", manifest.SourceAdapter!.Key);
        Assert.Equal("hmac_sha256", Assert.Single(manifest.SourceVerification.Schemes).Scheme);
    }

    [Fact]
    public void Parse_RejectsVerifiedWebhookThatAllowsUnverifiedUse()
    {
        string json = ManifestWithSourceAdapter("verified_webhook", schemeName: "hmac_sha256")
            .Replace("\"allow_unverified\": false", "\"allow_unverified\": true", StringComparison.Ordinal);

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("does not allow unverified use", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSourceAdapterConfigWithInvalidHttpHeaderName()
    {
        string json = MaximalManifest()
            .Replace("X-Hub-Signature-256", "bad header", StringComparison.Ordinal);

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => ParseWithRealSourceAdapterRegistry(Json(json)));

        Assert.Contains("valid HTTP header name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltinHttpDestinationSchema_RejectsUnknownConfiguration()
    {
        BuiltinIntegration http = Assert.Single(BuiltinCatalog.All);
        JsonElement schema = http.Manifest.DestinationConfigurationSchema!.Value;

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Parse_RejectsUnknownSourceAdapter()
    {
        string json = ManifestWithSourceAdapter("nonexistent_adapter", schemeName: "hmac_sha256");

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("is not registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSourceVerificationSchemesWithoutSourceAdapter()
    {
        string json = ValidManifest()
            .Replace("\"direction\": \"destination\"", "\"direction\": \"both\"", StringComparison.Ordinal)
            .Replace(
                "\"destination_configuration_schema\"",
                "\"source_configuration_schema\": { \"type\": \"object\", \"properties\": {}, \"additionalProperties\": true }, \"destination_configuration_schema\"",
                StringComparison.Ordinal)
            .Replace(
                "\"source_verification\": { \"allow_unverified\": true, \"schemes\": [] }",
                "\"source_verification\": { \"allow_unverified\": false, \"schemes\": [{\"scheme\":\"hmac_sha256\",\"required_config\":[],\"required_secret_refs\":[\"secret\"]}] }",
                StringComparison.Ordinal);

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("requires a source_adapter selection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSourceAdapterWithIncompatibleVerificationSchemes()
    {
        string json = ValidManifest()
            .Replace("\"direction\": \"destination\"", "\"direction\": \"both\"", StringComparison.Ordinal)
            .Replace(
                "\"destination_configuration_schema\"",
                "\"source_configuration_schema\": { \"type\": \"object\", \"properties\": {}, \"additionalProperties\": true }, \"destination_configuration_schema\"",
                StringComparison.Ordinal)
            .Replace(
                "\"source_verification\": { \"allow_unverified\": true, \"schemes\": [] }",
                """
                "source_verification": { "allow_unverified": true, "schemes": [] },
                "source_adapter": {
                  "key": "verified_webhook",
                  "contract_version": 1,
                  "config": { "signature_header": "X-Hub-Signature-256" }
                }
                """,
                StringComparison.Ordinal);

        var exception = Assert.Throws<IntegrationManifestValidationException>(
            () => Parse(Json(json)));

        Assert.Contains("requires source_verification.schemes to declare exactly", exception.Message, StringComparison.Ordinal);
    }

    private static string ManifestWithSourceAdapter(string adapterKey, string schemeName) => ValidManifest()
        .Replace("\"direction\": \"destination\"", "\"direction\": \"both\"", StringComparison.Ordinal)
        .Replace(
            "\"destination_configuration_schema\"",
            "\"source_configuration_schema\": { \"type\": \"object\", \"properties\": {}, \"additionalProperties\": true }, \"destination_configuration_schema\"",
            StringComparison.Ordinal)
        .Replace(
            "\"source_verification\": { \"allow_unverified\": true, \"schemes\": [] }",
            $$"""
            "source_verification": {
              "allow_unverified": false,
              "schemes": [{"scheme":"{{schemeName}}","required_config":[],"required_secret_refs":["secret"]}]
            },
            "source_adapter": {
              "key": "{{adapterKey}}",
              "contract_version": 1,
              "config": { "signature_header": "X-Hub-Signature-256" }
            }
            """,
            StringComparison.Ordinal);

    [Fact]
    public void FunctionalDocument_ExcludesPresentationAndIsOrderInsensitive()
    {
        var first = Parse(Json(ValidManifest()));
        var second = Parse(Json(ValidManifest()
            .Replace("Example API", "Improved Name", StringComparison.Ordinal)));

        Assert.True(JsonElement.DeepEquals(
            IntegrationManifestParser.ToFunctionalJson(first),
            IntegrationManifestParser.ToFunctionalJson(second)));
    }

    [Fact]
    public void FunctionalDocument_CanonicalizesSetLikeArrays()
    {
        var first = Parse(Json(SetManifest(
            required: "[\"region\",\"base_uri\"]",
            values: "[\"us\",\"eu\"]",
            schemes: """
                [
                  {"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]},
                  {"scheme":"api_key_header","required_config":["header_name"],"required_secret_refs":["api_key"]}
                ]
                """)));
        var second = Parse(Json(SetManifest(
            required: "[\"base_uri\",\"region\"]",
            values: "[\"eu\",\"us\"]",
            schemes: """
                [
                  {"scheme":"api_key_header","required_config":["header_name"],"required_secret_refs":["api_key"]},
                  {"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}
                ]
                """)));

        Assert.True(JsonElement.DeepEquals(
            IntegrationManifestParser.ToFunctionalJson(first),
            IntegrationManifestParser.ToFunctionalJson(second)));
    }

    [Fact]
    public void Parse_RejectsDuplicateEnumValues()
    {
        var exception = Assert.Throws<IntegrationManifestValidationException>(() => Parse(Json(SetManifest(
            required: "[\"base_uri\"]",
            values: "[\"eu\",\"us\",\"us\"]",
            schemes: "[]"))));

        Assert.Contains("enum must contain unique values", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsJsonBooleanDiagnosticField()
    {
        string json = ValidManifest().Replace(
            "\"presentation\":",
            "\"http_outcome\":{\"evaluator\":\"json_boolean\",\"field\":\"ok\",\"expected\":true,\"diagnostic_field\":\"error\",\"max_body_bytes\":65536},\"presentation\":",
            StringComparison.Ordinal);

        Assert.NotNull(Parse(Json(json)).HttpOutcome);
    }

    [Fact]
    public void Parse_RejectsNumericBoundsOutsideSupportedRange()
    {
        string json = ValidManifest().Replace(
            "{ \"type\": \"string\" }",
            "{ \"type\": \"number\", \"minimum\": 1e100 }",
            StringComparison.Ordinal);

        Assert.Throws<IntegrationManifestValidationException>(() => Parse(Json(json)));
    }

    [Fact]
    public void Parse_RejectsExplicitNullRequiredDirectionalSchema()
    {
        JsonObject manifest = JsonNode.Parse(ValidManifest())!.AsObject();
        manifest["destination_configuration_schema"] = null;

        Assert.Throws<IntegrationManifestValidationException>(() => Parse(Json(manifest.ToJsonString())));
    }

    [Fact]
    public void StoredManifest_RoundTripsThroughTheCanonicalSerializerContract()
    {
        IntegrationManifest parsed = ParseAsBootstrap(Json(MaximalManifest()));
        JsonElement stored = IntegrationManifestParser.ToJson(parsed);

        // The persisted wire format is snake_case, so a property that loses its
        // [JsonPropertyName] surfaces here as an unexpected camelCase name. These
        // assertions also fail when a new manifest property is added without being
        // populated below, keeping the round trip exercised over the whole contract.
        Assert.Equal(
            [
                "contract_version",
                "destination_authentication",
                "destination_configuration_schema",
                "direction",
                "http_outcome",
                "key",
                "manifest_schema_version",
                "presentation",
                "source_adapter",
                "source_configuration_schema",
                "source_verification",
            ],
            stored.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["authoring_presets", "description", "event_types", "name"],
            stored.GetProperty("presentation").EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["allow_unauthenticated", "schemes"],
            stored.GetProperty("destination_authentication").EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["required_config", "required_secret_refs", "scheme"],
            stored.GetProperty("destination_authentication").GetProperty("schemes")[0].EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["config", "contract_version", "key"],
            stored.GetProperty("source_adapter").EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));

        IntegrationManifest rehydrated =
            IntegrationManifestParser.DeserializeStored(stored.GetRawText());

        Assert.True(JsonElement.DeepEquals(stored, IntegrationManifestParser.ToJson(rehydrated)));
    }

    private static string ValidManifest() => """
        {
          "manifest_schema_version": 1,
          "key": "example_api",
          "contract_version": 1,
          "direction": "destination",
          "destination_configuration_schema": {
            "type": "object",
            "properties": { "base_uri": { "type": "string" } },
            "required": ["base_uri"],
            "additionalProperties": false
          },
          "source_verification": { "allow_unverified": true, "schemes": [] },
          "destination_authentication": { "allow_unauthenticated": true, "schemes": [] },
          "presentation": { "name": "Example API", "event_types": [], "authoring_presets": [] }
        }
        """;

    private static string SetManifest(string required, string values, string schemes) => $$$"""
        {
          "manifest_schema_version":1,
          "key":"set_api",
          "contract_version":1,
          "direction":"destination",
          "destination_configuration_schema":{
            "type":"object",
            "properties":{
              "base_uri":{"type":"string"},
              "region":{"type":"string","enum":{{{values}}}}
            },
            "required":{{{required}}},
            "additionalProperties":false
          },
          "source_verification":{"allow_unverified":true,"schemes":[]},
          "destination_authentication":{"allow_unauthenticated":false,"schemes":{{{schemes}}}},
          "presentation":{"name":"Set API","event_types":[],"authoring_presets":[]}
        }
        """;

    private static string MaximalManifest() => """
        {
          "manifest_schema_version":1,
          "key":"maximal_api",
          "contract_version":3,
          "direction":"both",
          "source_configuration_schema":{
            "type":"object",
            "properties":{
              "repository":{"type":"string","minLength":1,"maxLength":200},
              "include_forks":{"type":"boolean","enum":[true,false]}
            },
            "required":["repository"],
            "additionalProperties":false
          },
          "destination_configuration_schema":{
            "type":"object",
            "properties":{
              "base_uri":{"type":"string","format":"uri","minLength":8,"maxLength":200},
              "region":{"type":"string","enum":["eu","us"]},
              "port":{"type":"integer","minimum":1,"maximum":65535}
            },
            "required":["base_uri","region"],
            "additionalProperties":false
          },
          "source_verification":{
            "allow_unverified":false,
            "schemes":[
              {"scheme":"hmac_sha256","required_config":[],"required_secret_refs":["secret"]}
            ]
          },
          "destination_authentication":{
            "allow_unauthenticated":false,
            "schemes":[
              {"scheme":"api_key_header","required_config":["header_name"],"required_secret_refs":["api_key"]},
              {"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}
            ]
          },
          "source_adapter":{
            "key":"verified_webhook",
            "contract_version":1,
            "config":{
              "signature_header":"X-Hub-Signature-256",
              "signature_encoding":"hex",
              "signature_prefix":"sha256=",
              "delivery_id_header":"X-GitHub-Delivery",
              "event_type_header":"X-GitHub-Event",
              "event_type_action_field":"action"
            }
          },
          "http_outcome":{
            "evaluator":"json_boolean",
            "field":"ok",
            "expected":true,
            "diagnostic_field":"error",
            "max_body_bytes":65536
          },
          "presentation":{
            "name":"Maximal API",
            "description":"Populates every manifest property.",
            "event_types":["github.push","github.issues.opened"],
            "authoring_presets":[{"event_type":"github.push","transform":{"channel":"#general"}}]
          }
        }
        """;

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static IntegrationManifest ParseAsBootstrap(JsonElement document) =>
        IntegrationManifestParser.Parse(
            document,
            new FakeAuthSchemeRegistry(),
            new FakeSourceAdapterRegistry(),
            IntegrationManifestApplyAuthority.Bootstrap(Guid.NewGuid()));

    private static IntegrationManifest Parse(JsonElement document) =>
        IntegrationManifestParser.Parse(
            document,
            new FakeAuthSchemeRegistry(),
            new FakeSourceAdapterRegistry(),
            IntegrationManifestApplyAuthority.Operator);

    private static IntegrationManifest ParseWithRealSourceAdapterRegistry(JsonElement document) =>
        IntegrationManifestParser.Parse(
            document,
            new FakeAuthSchemeRegistry(),
            new Integrios.Infrastructure.Integrations.SourceAdapterRegistry(),
            IntegrationManifestApplyAuthority.Operator);

    private sealed class FakeAuthSchemeRegistry : IAuthSchemeRegistry
    {
        private static readonly IReadOnlyDictionary<string, IAuthSchemeHandler> Handlers =
            new Dictionary<string, IAuthSchemeHandler>(StringComparer.OrdinalIgnoreCase)
            {
                ["api_key_header"] = new FakeAuthSchemeHandler("api_key_header", ["header_name"], ["api_key"]),
                ["bearer_token"] = new FakeAuthSchemeHandler("bearer_token", [], ["token"]),
            };

        public IAuthSchemeHandler GetRequired(string scheme) =>
            Handlers.TryGetValue(scheme, out IAuthSchemeHandler? handler)
                ? handler
                : throw new InvalidOperationException();

        public bool TryGet(string scheme, out IAuthSchemeHandler handler) =>
            Handlers.TryGetValue(scheme, out handler!);
    }

    private sealed record FakeAuthSchemeHandler(
        string Name,
        IReadOnlyList<string> RequiredConfigFields,
        IReadOnlyList<string> RequiredSecretFields) : IAuthSchemeHandler
    {
        public void Apply(
            HttpRequestMessage request,
            JsonElement config,
            IReadOnlyDictionary<string, string> secrets) => throw new NotSupportedException();
    }

    private sealed class FakeSourceAdapterRegistry : ISourceAdapterRegistry
    {
        private static readonly IReadOnlyDictionary<(string Key, int ContractVersion), SourceAdapterRegistration> Registrations =
            new Dictionary<(string, int), SourceAdapterRegistration>
            {
                [("verified_webhook", 1)] = new(
                    "verified_webhook", 1, AuthoringSafe: true, AllowsUnverifiedUse: false,
                    ["hmac_sha256"], ValidateConfig: RequireObjectConfig),
                [("github_native", 1)] = new(
                    "github_native", 1, AuthoringSafe: false, AllowsUnverifiedUse: false,
                    ["hmac_sha256"], ValidateConfig: RequireObjectConfig),
            };

        public bool TryGet(string key, int contractVersion, out SourceAdapterRegistration registration) =>
            Registrations.TryGetValue((key, contractVersion), out registration!);

        private static void RequireObjectConfig(JsonElement config)
        {
            if (config.ValueKind != JsonValueKind.Object)
                throw new IntegrationManifestValidationException("source_adapter.config must be an object.");
        }
    }
}
