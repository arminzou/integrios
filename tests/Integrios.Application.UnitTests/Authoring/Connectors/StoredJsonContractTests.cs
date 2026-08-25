using System.Text.Json;
using System.Text.RegularExpressions;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.UnitTests;

// ConnectorManifest round-trips through a jsonb column rather than through an HTTP host, so no
// host naming policy reaches it. It used to hold its key names in [JsonPropertyName] attributes;
// this Fact holds the same contract now that the names come from serializer options instead. A
// regression here silently rewrites stored rows.
public sealed partial class StoredJsonContractTests
{
    [Fact]
    public void ConnectorManifest_RoundTripsStoredKeysUnchanged()
    {
        const string stored = """
            {
              "manifest_schema_version": 1,
              "key": "github",
              "contract_version": 1,
              "direction": "source",
              "source_verification": {
                "allow_unverified": false,
                "schemes": [
                  { "scheme": "hmac_sha256", "required_config": [], "required_secret_refs": ["secret"] }
                ]
              },
              "destination_authentication": { "allow_unauthenticated": true, "schemes": [] },
              "source_contracts": [{ "key": "github_webhook", "contract_version": 1, "config": {} }],
              "presentation": { "name": "GitHub", "event_types": ["issues.opened"], "authoring_presets": [] }
            }
            """;

        ConnectorManifest manifest = ConnectorManifestParser.DeserializeStored(stored);
        JsonElement written = ConnectorManifestParser.ToJson(manifest);

        manifest.ManifestSchemaVersion.ShouldBe(1);
        manifest.SourceContracts.ShouldHaveSingleItem().Key.ShouldBe("github_webhook");
        manifest.SourceVerification.Schemes[0].RequiredSecretRefs.ShouldBe(new[] { "secret" });

        AssertAllPropertyNamesAreSnakeCase(written, path: "$");

        JsonElement scheme = written.GetProperty("source_verification").GetProperty("schemes")[0];
        scheme.TryGetProperty("required_secret_refs", out _).ShouldBeTrue();
        written.GetProperty("presentation").TryGetProperty("event_types", out _).ShouldBeTrue();
        written.GetProperty("source_contracts")[0].TryGetProperty("contract_version", out _).ShouldBeTrue();
    }

    // Stored keys are snake_case and stable; a flat top-level pin misses nested keys and breaks on
    // any legitimate addition. This scans every key at every depth instead.
    private static void AssertAllPropertyNamesAreSnakeCase(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    SnakeCase().IsMatch(property.Name).ShouldBeTrue(
                        $"Stored JSON keys must be snake_case. Found '{property.Name}' at {path}.{property.Name}.");
                    AssertAllPropertyNamesAreSnakeCase(property.Value, $"{path}.{property.Name}");
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AssertAllPropertyNamesAreSnakeCase(item, $"{path}[{index}]");
                    index++;
                }
                break;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCase();
}
