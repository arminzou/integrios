using System.Text.Json;
using Integrios.Domain.Integrations;

namespace Integrios.Domain.UnitTests;

// ConnectionSchemeSelection round-trips through a jsonb column rather than through an HTTP host, so
// no host naming policy reaches it. It used to hold its key names in [JsonPropertyName] attributes;
// this Fact pins the same bytes now that the names come from serializer options instead. A
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
}
