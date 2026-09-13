using System.Text.Json;
using System.Text.Json.Serialization;

namespace Integrios.Domain.Enums;

// Serialized as snake_case strings (see EventStatusJsonConverter). The integer backing is not
// a contract; member order and value are meaningless.
[JsonConverter(typeof(EventStatusJsonConverter))]
public enum EventStatus
{
    Accepted,
    Processing,
    Routed,
    Unrouted,
    Failed,
    DeadLettered
}

// Single source of truth for the canonical snake_case spelling of each EventStatus, shared by
// the JSON wire (EventStatusJsonConverter) and the database status column.
public static class EventStatusMap
{
    private static readonly IReadOnlyDictionary<EventStatus, string> ToDb =
        new Dictionary<EventStatus, string>
        {
            [EventStatus.Accepted] = "accepted",
            [EventStatus.Processing] = "processing",
            [EventStatus.Routed] = "routed",
            [EventStatus.Unrouted] = "unrouted",
            [EventStatus.Failed] = "failed",
            [EventStatus.DeadLettered] = "dead_lettered",
        };

    private static readonly IReadOnlyDictionary<string, EventStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    // The canonical spellings, in declaration order. The API document describes the wire vocabulary
    // from this same map rather than restating it.
    public static IReadOnlyList<string> DbValues { get; } = [.. ToDb.Values];

    public static string ToDbValue(EventStatus status) =>
        ToDb.TryGetValue(status, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped event status.");

    public static EventStatus FromDbValue(string value) =>
        FromDb.TryGetValue(value, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown event status.");
}

// Serializes EventStatus as its canonical snake_case string (via EventStatusMap), so the API
// speaks the same vocabulary as the database.
public sealed class EventStatusJsonConverter : JsonConverter<EventStatus>
{
    public override EventStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EventStatusMap.FromDbValue(reader.GetString() ?? throw new JsonException("Expected a string event status."));

    public override void Write(Utf8JsonWriter writer, EventStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EventStatusMap.ToDbValue(value));
}
