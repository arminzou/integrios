using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Domain.UnitTests;

// SourceVerification and DestinationAuthentication round-trip through a jsonb column rather than
// through an HTTP host, so no host naming policy reaches them. Their shared shape used to be one
// type holding its key names in [JsonPropertyName] attributes; this Fact pins the same bytes now
// that the names come from serializer options instead. A regression here silently rewrites stored
// rows. Both types are identical in shape, so pinning one is sufficient.
public sealed class StoredJsonContractTests
{
    [Fact]
    public void SourceVerification_StoresSnakeCaseKeys()
    {
        var selection = new SourceVerification
        {
            Scheme = "hmac_sha256",
            Config = JsonSerializer.Deserialize<JsonElement>("""{"signature_header":"X-Hub-Signature-256"}"""),
            SecretRefs = JsonSerializer.Deserialize<JsonElement>("""{"secret":"env:GH_SECRET"}"""),
        };

        string json = JsonSerializer.Serialize(selection, StoredJson.Options);

        Assert.Equal(
            """{"scheme":"hmac_sha256","config":{"signature_header":"X-Hub-Signature-256"},"secret_refs":{"secret":"env:GH_SECRET"}}""",
            json);

        SourceVerification? restored = JsonSerializer.Deserialize<SourceVerification>(
            json, StoredJson.Options);

        Assert.NotNull(restored);
        Assert.Equal("hmac_sha256", restored.Scheme);
        Assert.Equal("env:GH_SECRET", restored.SecretRefs.GetProperty("secret").GetString());
    }
}
