using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record SubscriberKeyDto
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid DestinationId { get; init; }
    public required string Name { get; init; }
    public required string KeyPrefix { get; init; }
    public required string State { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }

    public static SubscriberKeyDto From(SubscriberKey key) => new()
    {
        Id = key.Id,
        TenantId = key.TenantId,
        DestinationId = key.DestinationId,
        Name = key.Name,
        KeyPrefix = key.KeyPrefix,
        State = SubscriberKeyState.From(key),
        Description = key.Description,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt,
        RevokedAt = key.RevokedAt,
    };
}
