using System.Text.Json;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Abstractions;

public interface IConnectionRepository
{
    Task<Connection> CreateAsync(Connection connection, CancellationToken cancellationToken = default);
    Task<Connection?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Connection> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default);
    Task<Connection?> UpdateAsync(Guid tenantId, Guid id, string name, JsonElement config, string? environment, string? description, CancellationToken cancellationToken = default);
    Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);
}
