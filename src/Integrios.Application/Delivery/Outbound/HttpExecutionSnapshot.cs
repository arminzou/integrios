using System.Text.Json.Serialization;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Delivery;

// Correlates the destination base_uri, request shape, and destination authentication that a
// EventDelivery was fanned out with, so every retry replays the exact request the first
// attempt would have made even if the Subscription or Connection changes afterward. Serialize and
// deserialize with StoredJson.Options - see HttpDeliveryConfiguration for why.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpExecutionSnapshot
{
    public const int CurrentVersion = 1;

    public required int Version { get; init; }
    public required string BaseUri { get; init; }
    public required HttpDeliveryConfiguration Request { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectionSchemeSelection? DestinationAuthentication { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HttpSuccessRule? HttpSuccess { get; init; }
}
