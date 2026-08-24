using System.Text.Json;

namespace Integrios.Application.Authoring.Connections;

public sealed record SourceVerificationInput
{
    public required string Scheme { get; init; }
    public JsonElement Config { get; init; }

    public JsonElement SecretRefs { get; init; }
}
