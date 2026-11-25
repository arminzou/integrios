namespace Integrios.Application.PullDelivery;

public interface IActiveSubscriberKeyLookup
{
    Task<SubscriberIdentity?> FindActiveByKeyHashAsync(string keyHash, CancellationToken cancellationToken);
}

public sealed record SubscriberIdentity
{
    public required Guid KeyId { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid DestinationId { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}
