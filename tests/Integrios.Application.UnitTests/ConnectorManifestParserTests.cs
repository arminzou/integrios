using System.Text.Json;
using System.Text.Json.Nodes;
using Integrios.Application.Delivery;
using Integrios.Application.Bootstrap;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.UnitTests;

public sealed class ConnectorManifestParserTests
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

        manifest.Key.ShouldBe("example_api");
        manifest.ContractVersion.ShouldBe(2);
        manifest.DestinationAuthentication.Schemes.ShouldHaveSingleItem().Scheme.ShouldBe("bearer_token");
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

        var exception = Should.Throw<ConnectorManifestValidationException>(
            () => Parse(Json(json)));

        exception.Message.ShouldContain("unsupported JSON Schema keyword", Case.Sensitive);
    }

    [Fact]
    public void Parse_RejectsNestedObjectSchemas()
    {
        string json = ValidManifest().Replace(
            "\"type\": \"string\"",
            "\"type\": \"object\", \"properties\": {}",
            StringComparison.Ordinal);

        var exception = Should.Throw<ConnectorManifestValidationException>(
            () => Parse(Json(json)));

        exception.Message.ShouldContain("nested object schemas", Case.Sensitive);
    }

    [Fact]
    public void Parse_RejectsUnknownTopLevelProperty()
    {
        string json = ValidManifest().Replace(
            "\"manifest_schema_version\": 1,",
            "\"manifest_schema_version\": 1, \"extension\": true,",
            StringComparison.Ordinal);

        Should.Throw<ConnectorManifestValidationException>(
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

        Should.Throw<ConnectorManifestValidationException>(
            () => Parse(Json(json)));
    }

    [Fact]
    public void Parse_RejectsUnknownOrMisdeclaredPlatformScheme()
    {
        string json = ValidManifest().Replace(
            "\"destination_authentication\": { \"allow_unauthenticated\": true, \"schemes\": [] }",
            "\"destination_authentication\": { \"allow_unauthenticated\": true, \"schemes\": [{\"scheme\":\"bearer_token\",\"required_config\":[],\"required_secret_refs\":[]}] }",
            StringComparison.Ordinal);

        var exception = Should.Throw<ConnectorManifestValidationException>(
            () => Parse(Json(json)));

        exception.Message.ShouldContain("not a supported platform contract", Case.Sensitive);
    }

    [Fact]
    public void Parse_AcceptsDeclaredSourceContract()
    {
        var manifest = Parse(Json(ManifestWithSourceContract("verified_webhook", schemeName: "hmac_sha256")));

        manifest.SourceContracts.ShouldHaveSingleItem().Key.ShouldBe("verified_webhook");
        manifest.SourceVerification.Schemes.ShouldHaveSingleItem().Scheme.ShouldBe("hmac_sha256");
    }

    [Fact]
    public void BuiltinHttpDestinationSchema_RejectsUnknownConfiguration()
    {
        BuiltinConnector http = BuiltinCatalog.All.Where(item => item.Manifest.Key == "http").ShouldHaveSingleItem();
        JsonElement schema = http.Manifest.DestinationConfigurationSchema!.Value;

        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void BuiltinGitHub_IsPinnedToTheCompiledWebhookContract()
    {
        BuiltinConnector github = BuiltinCatalog.All.Where(item => item.Manifest.Key == "github").ShouldHaveSingleItem();

        github.Id.ShouldBe(BuiltinCatalog.GitHubId);
        github.Manifest.ContractVersion.ShouldBe(BuiltinCatalog.GitHubContractVersion);
        github.Manifest.SourceContracts.ShouldHaveSingleItem().Key.ShouldBe("github_webhook");
        github.Manifest.SourceVerification.Schemes.ShouldHaveSingleItem().Scheme.ShouldBe("hmac_sha256");
    }

    [Fact]
    public void Parse_AcceptsDeclarativeSourceContractWithSchemaAndMapping()
    {
        string json = ManifestWithDeclarativeSourceContract(
            """{"type":"object","properties":{"amount":{"type":"integer"}},"required":["amount"],"additionalProperties":true}""",
            """{"engine":"jsonata","version":"1","expression":"{ \"event_type\": \"payment.created\", \"payload\": $ }"}""");

        var manifest = Parse(Json(json));

        ConnectorSourceContractManifest entry = manifest.SourceContracts.ShouldHaveSingleItem();
        entry.Key.ShouldBe("event_json");
        entry.Schema.HasValue.ShouldBeTrue();
        entry.Mapping!.Engine.ShouldBe("jsonata");
    }

    [Fact]
    public void Parse_RejectsDeclarativeSourceContractWithInvalidMappingSyntax()
    {
        string json = ManifestWithDeclarativeSourceContract(
            schema: null,
            mapping: """{"engine":"jsonata","version":"1","expression":"{ "}""");

        var exception = Should.Throw<ConnectorManifestValidationException>(() => Parse(Json(json)));

        exception.Message.ShouldContain("Invalid JSONata expression", Case.Sensitive);
    }

    private static string ManifestWithDeclarativeSourceContract(string? schema, string? mapping) => $$"""
        {
          "manifest_schema_version": 1,
          "key": "event_api_example",
          "contract_version": 1,
          "direction": "source",
          "source_configuration_schema": { "type": "object", "properties": {}, "additionalProperties": true },
          "source_verification": { "allow_unverified": true, "schemes": [] },
          "destination_authentication": { "allow_unauthenticated": true, "schemes": [] },
          "source_contracts": [{
            "key": "event_json",
            "contract_version": 1,
            "config": {}{{(schema is null ? "" : $",\n            \"schema\": {schema}")}}{{(mapping is null ? "" : $",\n            \"mapping\": {mapping}")}}
          }],
          "presentation": { "name": "Event API Example", "event_types": [], "authoring_presets": [] }
        }
        """;

    private static string ManifestWithSourceContract(string contractKey, string schemeName) => ValidManifest()
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
            "source_contracts": [{
              "key": "{{contractKey}}",
              "contract_version": 1,
              "config": { "signature_header": "X-Hub-Signature-256" }
            }]
            """,
            StringComparison.Ordinal);

    [Fact]
    public void FunctionalDocument_ExcludesPresentationAndIsOrderInsensitive()
    {
        var first = Parse(Json(ValidManifest()));
        var second = Parse(Json(ValidManifest()
            .Replace("Example API", "Improved Name", StringComparison.Ordinal)));

        JsonElement.DeepEquals(
            ConnectorManifestParser.ToFunctionalJson(first),
            ConnectorManifestParser.ToFunctionalJson(second)).ShouldBeTrue();
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

        JsonElement.DeepEquals(
            ConnectorManifestParser.ToFunctionalJson(first),
            ConnectorManifestParser.ToFunctionalJson(second)).ShouldBeTrue();
    }

    [Fact]
    public void FunctionalDocument_CanonicalizesSourceContractSchemaSetLikeArrays()
    {
        var first = Parse(Json(ManifestWithDeclarativeSourceContract(
            schema: """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}},"required":["b","a"],"additionalProperties":true}""",
            mapping: null)));
        var second = Parse(Json(ManifestWithDeclarativeSourceContract(
            schema: """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}},"required":["a","b"],"additionalProperties":true}""",
            mapping: null)));

        JsonElement.DeepEquals(
            ConnectorManifestParser.ToFunctionalJson(first),
            ConnectorManifestParser.ToFunctionalJson(second)).ShouldBeTrue();
    }

    [Fact]
    public void Parse_RejectsDuplicateEnumValues()
    {
        var exception = Should.Throw<ConnectorManifestValidationException>(() => Parse(Json(SetManifest(
            required: "[\"base_uri\"]",
            values: "[\"eu\",\"us\",\"us\"]",
            schemes: "[]"))));

        exception.Message.ShouldContain("enum must contain unique values", Case.Sensitive);
    }

    [Fact]
    public void Parse_AcceptsJsonBooleanDiagnosticField()
    {
        string json = ValidManifest().Replace(
            "\"presentation\":",
            "\"http_success\":{\"evaluator\":\"json_boolean\",\"field\":\"ok\",\"expected\":true,\"diagnostic_field\":\"error\",\"max_body_bytes\":65536},\"presentation\":",
            StringComparison.Ordinal);

        Parse(Json(json)).HttpSuccess.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_RejectsNumericBoundsOutsideSupportedRange()
    {
        string json = ValidManifest().Replace(
            "{ \"type\": \"string\" }",
            "{ \"type\": \"number\", \"minimum\": 1e100 }",
            StringComparison.Ordinal);

        Should.Throw<ConnectorManifestValidationException>(() => Parse(Json(json)));
    }

    [Fact]
    public void Parse_RejectsExplicitNullRequiredDirectionalSchema()
    {
        JsonObject manifest = JsonNode.Parse(ValidManifest())!.AsObject();
        manifest["destination_configuration_schema"] = null;

        Should.Throw<ConnectorManifestValidationException>(() => Parse(Json(manifest.ToJsonString())));
    }

    [Fact]
    public void StoredManifest_RoundTripsThroughTheCanonicalSerializerContract()
    {
        ConnectorManifest parsed = ParseAsBootstrap(Json(MaximalManifest()));
        JsonElement stored = ConnectorManifestParser.ToJson(parsed);

        // The persisted wire format is snake_case, so a property that loses its
        // [JsonPropertyName] surfaces here as an unexpected camelCase name. These
        // assertions also fail when a new manifest property is added without being
        // populated below, keeping the round trip exercised over the whole contract.
        stored.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(
            [
                "contract_version",
                "destination_authentication",
                "destination_configuration_schema",
                "direction",
                "http_success",
                "key",
                "manifest_schema_version",
                "presentation",
                "source_configuration_schema",
                "source_contracts",
                "source_verification",
            ]);
        stored.GetProperty("presentation").EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["authoring_presets", "description", "event_types", "name"]);
        stored.GetProperty("destination_authentication").EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["allow_unauthenticated", "schemes"]);
        stored.GetProperty("destination_authentication").GetProperty("schemes")[0].EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["required_config", "required_secret_refs", "scheme"]);
        stored.GetProperty("source_contracts")[0].EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["config", "contract_version", "key"]);

        ConnectorManifest rehydrated =
            ConnectorManifestParser.DeserializeStored(stored.GetRawText());

        JsonElement.DeepEquals(stored, ConnectorManifestParser.ToJson(rehydrated)).ShouldBeTrue();
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
          "source_contracts":[{
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
          }],
          "http_success":{
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

    private static readonly ITransformEvaluator MappingEvaluator = new Integrios.Infrastructure.Transforms.JsonataTransformEvaluator();

    private static ConnectorManifest ParseAsBootstrap(JsonElement document) =>
        ConnectorManifestParser.Parse(
            document,
            new FakeAuthSchemeRegistry(),
            MappingEvaluator,
            ConnectorManifestApplyAuthority.Bootstrap(Guid.NewGuid()));

    private static ConnectorManifest Parse(JsonElement document) =>
        ConnectorManifestParser.Parse(
            document,
            new FakeAuthSchemeRegistry(),
            MappingEvaluator,
            ConnectorManifestApplyAuthority.Operator);

    private sealed class FakeAuthSchemeRegistry : IDestinationAuthenticatorRegistry
    {
        private static readonly IReadOnlyDictionary<string, IDestinationAuthenticator> Handlers =
            new Dictionary<string, IDestinationAuthenticator>(StringComparer.OrdinalIgnoreCase)
            {
                ["api_key_header"] = new FakeAuthSchemeHandler("api_key_header", ["header_name"], ["api_key"]),
                ["bearer_token"] = new FakeAuthSchemeHandler("bearer_token", [], ["token"]),
            };

        public IDestinationAuthenticator GetRequired(string scheme) =>
            Handlers.TryGetValue(scheme, out IDestinationAuthenticator? handler)
                ? handler
                : throw new InvalidOperationException();

        public bool TryGet(string scheme, out IDestinationAuthenticator handler) =>
            Handlers.TryGetValue(scheme, out handler!);
    }

    private sealed record FakeAuthSchemeHandler(
        string Name,
        IReadOnlyList<string> RequiredConfigFields,
        IReadOnlyList<string> RequiredSecretFields) : IDestinationAuthenticator
    {
        public IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config) => [];

        public void Apply(
            IDictionary<string, string> headers,
            JsonElement config,
            IReadOnlyDictionary<string, string> secrets) => throw new NotSupportedException();
    }

}
