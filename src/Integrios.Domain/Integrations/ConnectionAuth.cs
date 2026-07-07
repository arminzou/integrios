using System.Text.Json;

namespace Integrios.Domain.Integrations;

public sealed record ConnectionAuth
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }
    public required JsonElement SecretRefs { get; init; }
}
