using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.SubscriberKeys;

public interface ISubscriberKeyRepository
{
    Task<SubscriberKey> CreateAsync(SubscriberKey subscriberKey, CancellationToken cancellationToken);
    Task<SubscriberKey?> GetByIdAsync(Guid tenantId, Guid destinationId, Guid id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<SubscriberKey> Items, string? NextCursor)> ListByDestinationAsync(Guid tenantId, Guid destinationId, SubscriberKeyListState? state, string? afterCursor, int limit, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid tenantId, Guid destinationId, Guid id, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid tenantId, Guid destinationId, Guid id, CancellationToken cancellationToken);
}
