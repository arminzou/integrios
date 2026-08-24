using System.Text.Json;

namespace Integrios.Domain.ValueObjects;

public static class StoredJson
{
    // The stored form of every jsonb-backed Domain type is part of its contract: it round-trips
    // through columns that no HTTP host naming policy reaches. Every call site that reads or
    // writes a stored JSON column must serialize with these options.
    public static readonly JsonSerializerOptions Options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
