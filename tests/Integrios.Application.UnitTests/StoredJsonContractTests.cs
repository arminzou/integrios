using System.Text.Json;
using System.Text.RegularExpressions;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;

namespace Integrios.Application.UnitTests;

// IntegrationManifest round-trips through a jsonb column rather than through an HTTP host, so no
// host naming policy reaches it. It used to hold its key names in [JsonPropertyName] attributes;
// this Fact holds the same contract now that the names come from serializer options instead. A
// regression here silently rewrites stored rows.
public sealed partial class StoredJsonContractTests
{
    [Fact]
    public void IntegrationManifest_RoundTripsStoredKeysUnchanged()
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
              "source_adapter": { "key": "github_webhook", "contract_version": 1, "config": {} },
              "presentation": { "name": "GitHub", "event_types": ["issues.opened"], "authoring_presets": [] }
            }
            """;

        IntegrationManifest manifest = IntegrationManifestParser.DeserializeStored(stored);
        JsonElement written = IntegrationManifestParser.ToJson(manifest);

        Assert.Equal(1, manifest.ManifestSchemaVersion);
        Assert.Equal("github_webhook", manifest.SourceAdapter?.Key);
        Assert.Equal(["secret"], manifest.SourceVerification.Schemes[0].RequiredSecretRefs);

        AssertAllPropertyNamesAreSnakeCase(written, path: "$");

        JsonElement scheme = written.GetProperty("source_verification").GetProperty("schemes")[0];
        Assert.True(scheme.TryGetProperty("required_secret_refs", out _));
        Assert.True(written.GetProperty("presentation").TryGetProperty("event_types", out _));
        Assert.True(written.GetProperty("source_adapter").TryGetProperty("contract_version", out _));
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
                    Assert.True(
                        SnakeCase().IsMatch(property.Name),
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
