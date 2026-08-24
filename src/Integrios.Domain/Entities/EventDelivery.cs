using System.Text.Json;
using Integrios.Domain.Enums;

namespace Integrios.Domain.Entities;

public sealed record EventDelivery
{
    public required Guid Id { get; init; }
    public required Guid EventId { get; init; }
    public required Guid SubscriptionId { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public EventDeliveryStatus Status { get; init; } = EventDeliveryStatus.Pending;
    public int LifetimeAttemptCount { get; init; }
    public int RetryCycleAttemptCount { get; init; }
    public DateTimeOffset? DeliverAfter { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public JsonElement? MappingConfigSnapshot { get; init; }
    public string? Traceparent { get; init; }
    public required string ConnectorKey { get; init; }
    public Guid? ActiveAttemptId { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public required JsonElement HttpExecutionSnapshot { get; init; }
}
