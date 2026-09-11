using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class ConnectorManifestParserTests
{
    [Fact]
    public void Parse_RoundTripsTheAuthoritativeVersionOneShape()
    {
        var parsed = Parse(Json(ValidManifest()));
        JsonElement stored = ConnectorManifestParser.ToJson(parsed);

        stored.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(
        [
            "contract_version",
            "destination_authentication",
            "destination_configuration_schema",
            "direction",
            "key",
            "manifest_schema_version",
            "presentation",
            "source_configuration_schema",
            "source_verification",
        ]);
        JsonElement.DeepEquals(
            stored,
            ConnectorManifestParser.ToJson(ConnectorManifestParser.DeserializeStored(stored.GetRawText())))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("source_contracts")]
    [InlineData("http_success")]
    public void Parse_RejectsRetiredConnectorOwnedFields(string field)
    {
        string document = ValidManifest().Replace(
            "\"presentation\":",
            $"\"{field}\":[],\"presentation\":",
            StringComparison.Ordinal);

        Should.Throw<ConnectorManifestValidationException>(() => Parse(Json(document)))
            .Message.ShouldContain($"unsupported property '{field}'", Case.Sensitive);
    }

    [Fact]
    public void Parse_RejectsSchemaForADirectionTheConnectorDoesNotSupport()
    {
        string document = ValidManifest().Replace(
            "\"direction\":\"both\"",
            "\"direction\":\"destination\"",
            StringComparison.Ordinal);

        Should.Throw<ConnectorManifestValidationException>(() => Parse(Json(document)))
            .Message.ShouldContain("source_configuration_schema is not allowed", Case.Sensitive);
    }

    [Fact]
    public void Parse_CanonicalizesSetLikeSchemaFields()
    {
        var first = Parse(Json(ValidManifest()));
        var second = Parse(Json(ValidManifest()
            .Replace("[\"base_uri\",\"region\"]", "[\"region\",\"base_uri\"]", StringComparison.Ordinal)
            .Replace("[\"eu\",\"us\"]", "[\"us\",\"eu\"]", StringComparison.Ordinal)));

        JsonElement.DeepEquals(
            ConnectorManifestParser.ToFunctionalJson(first),
            ConnectorManifestParser.ToFunctionalJson(second)).ShouldBeTrue();
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static Integrios.Domain.ValueObjects.ConnectorManifest Parse(JsonElement document) =>
        ConnectorManifestParser.Parse(document, new FakeAuthSchemeRegistry());

    private static string ValidManifest() => """
        {
          "manifest_schema_version":1,
          "key":"example_api",
          "contract_version":1,
          "direction":"both",
          "source_configuration_schema":{"type":"object","properties":{},"additionalProperties":true},
          "destination_configuration_schema":{"type":"object","properties":{"base_uri":{"type":"string"},"region":{"type":"string","enum":["eu","us"]}},"required":["base_uri","region"],"additionalProperties":false},
          "source_verification":{"allow_unverified":true,"schemes":[]},
          "destination_authentication":{"allow_unauthenticated":true,"schemes":[]},
          "presentation":{"name":"Example API","event_types":[],"authoring_presets":[]}
        }
        """;

    private sealed class FakeAuthSchemeRegistry : IDestinationAuthenticatorRegistry
    {
        public IDestinationAuthenticator GetRequired(string scheme) => throw new InvalidOperationException();

        public bool TryGet(string scheme, out IDestinationAuthenticator handler)
        {
            handler = null!;
            return false;
        }
    }
}
