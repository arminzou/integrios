using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integrios.Domain.Integrations;

public sealed record ConnectionSchemeSelection
{
    [JsonPropertyName("scheme")]
    public required string Scheme { get; init; }
    [JsonPropertyName("config")]
    public required JsonElement Config { get; init; }
    [JsonPropertyName("secret_refs")]
    public required JsonElement SecretRefs { get; init; }
}
