using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integrios.Application.Connections;

public sealed record ConnectionAuthInput
{
    public required string Scheme { get; init; }
    public JsonElement Config { get; init; }

    [JsonPropertyName("secret_refs")]
    public JsonElement SecretRefs { get; init; }
}
