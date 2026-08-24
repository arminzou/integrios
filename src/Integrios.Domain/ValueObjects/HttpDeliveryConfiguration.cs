using System.Text.Json.Serialization;

namespace Integrios.Domain.ValueObjects;

// Stored in subscriptions.http_delivery and nested inside HttpExecutionSnapshot in
// event_deliveries.http_execution_snapshot. Neither column is reached by an HTTP host's
// naming policy, so every call site that serializes or deserializes this type - or a type that
// nests it - must pass Integrios.Domain.ValueObjects.StoredJson.Options.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpDeliveryConfiguration
{
    public const int CurrentVersion = 1;

    public required int Version { get; init; }
    public required string Method { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public required string Body { get; init; }

    public static HttpDeliveryConfiguration Default { get; } = new()
    {
        Version = CurrentVersion,
        Method = "POST",
        Headers = new Dictionary<string, string>(),
        Body = "json"
    };
}
