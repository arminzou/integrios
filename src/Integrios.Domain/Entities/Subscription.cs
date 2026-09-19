using System.Text.Json;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Domain.Entities;

public sealed record Subscription
{
    public required Guid Id { get; init; }
    public required Guid TopicId { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    /// The Event types this Subscription routes, each one its Topic's Sources declare. Never empty;
    /// matched exactly, ignoring case.
    public required IReadOnlyList<string> EventTypes { get; init; }
    public required Guid DestinationId { get; init; }
    public JsonElement? MappingConfig { get; init; }
    public HttpDeliveryConfiguration? HttpDelivery { get; init; }
    public HttpSuccessRule? HttpSuccess { get; init; }
    public required EnablementStatus Status { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public required int OrderIndex { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
