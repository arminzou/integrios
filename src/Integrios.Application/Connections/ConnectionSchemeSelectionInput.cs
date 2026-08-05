using System.Text.Json;

namespace Integrios.Application.Connections;

public sealed record ConnectionSchemeSelectionInput
{
    public required string Scheme { get; init; }
    public JsonElement Config { get; init; }

    public JsonElement SecretRefs { get; init; }
}
