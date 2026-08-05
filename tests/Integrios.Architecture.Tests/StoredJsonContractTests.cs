using System.Text.Json;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;

namespace Integrios.Architecture.Tests;

// Two Domain types round-trip through jsonb columns rather than through an HTTP host, so no host
// naming policy reaches them. They used to hold their key names in [JsonPropertyName] attributes;
// these Facts pin the same bytes now that the names come from serializer options instead. A
// regression here silently rewrites stored rows.
public sealed class StoredJsonContractTests
{
    [Fact]
    public void ConnectionSchemeSelection_StoresSnakeCaseKeys()
    {
        var selection = new ConnectionSchemeSelection
        {
            Scheme = "hmac_sha256",
            Config = JsonSerializer.Deserialize<JsonElement>("""{"signature_header":"X-Hub-Signature-256"}"""),
            SecretRefs = JsonSerializer.Deserialize<JsonElement>("""{"secret":"env:GH_SECRET"}"""),
        };

        string json = JsonSerializer.Serialize(selection, ConnectionSchemeSelection.StoredJson);

        Assert.Equal(
            """{"scheme":"hmac_sha256","config":{"signature_header":"X-Hub-Signature-256"},"secret_refs":{"secret":"env:GH_SECRET"}}""",
            json);

        ConnectionSchemeSelection? restored = JsonSerializer.Deserialize<ConnectionSchemeSelection>(
            json, ConnectionSchemeSelection.StoredJson);

        Assert.NotNull(restored);
        Assert.Equal("hmac_sha256", restored.Scheme);
        Assert.Equal("env:GH_SECRET", restored.SecretRefs.GetProperty("secret").GetString());
    }

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

        string[] topLevelKeys = written.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [
                "contract_version",
                "destination_authentication",
                "direction",
                "key",
                "manifest_schema_version",
                "presentation",
                "source_adapter",
                "source_verification",
            ],
            topLevelKeys);

        JsonElement scheme = written.GetProperty("source_verification").GetProperty("schemes")[0];
        Assert.True(scheme.TryGetProperty("required_secret_refs", out _));
        Assert.True(written.GetProperty("presentation").TryGetProperty("event_types", out _));
        Assert.True(written.GetProperty("source_adapter").TryGetProperty("contract_version", out _));
    }
}
