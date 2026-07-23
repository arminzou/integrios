using System.Text.Json;
using Integrios.Domain.Integrations;

namespace Integrios.Domain.Delivery;

public sealed record SubscriptionDelivery
{
    public required Guid Id { get; init; }
    public required Guid EventId { get; init; }
    public required Guid SubscriptionId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public required string DestinationUrl { get; init; }
    public required string IntegrationKey { get; init; }
    public ConnectionAuth? DestinationAuth { get; init; }
    public JsonElement? TransformConfigSnapshot { get; init; }
    public required string Status { get; init; }
    public required int AttemptCount { get; init; }
    public DateTimeOffset? DeliverAfter { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
