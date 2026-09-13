using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Destinations;

public interface IDestinationRepository
{
    Task<Destination> CreateAsync(Destination destination, CancellationToken cancellationToken);
    Task<Destination?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<DestinationListRow> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, DestinationListFilter filter, string? afterCursor, int limit, CancellationToken cancellationToken);
    Task<bool> HasActiveSubscriptionsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<Destination?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string name,
        JsonElement configuration,
        DestinationAuthentication? authentication,
        string? environment,
        string? description,
        CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}
