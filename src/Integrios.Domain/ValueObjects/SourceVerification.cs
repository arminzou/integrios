using System.Text.Json;

namespace Integrios.Domain.ValueObjects;

public sealed record SourceVerification
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }
    public required JsonElement SecretRefs { get; init; }
}
