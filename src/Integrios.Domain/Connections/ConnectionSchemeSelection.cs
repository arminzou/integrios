using System.Text.Json;

namespace Integrios.Domain.Connections;

public sealed record ConnectionSchemeSelection
{
    // The stored form of this type is part of its contract: it round-trips through the
    // connections.source_verification and connections.destination_authentication jsonb columns,
    // which no HTTP host naming policy reaches. Every call site that reads or writes those
    // columns must serialize with these options.
    public static readonly JsonSerializerOptions StoredJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }
    public required JsonElement SecretRefs { get; init; }
}
