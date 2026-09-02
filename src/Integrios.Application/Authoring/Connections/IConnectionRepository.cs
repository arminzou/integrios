using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connections;

public interface IConnectionRepository
{
    Task<Connection> CreateAsync(Connection connection, CancellationToken cancellationToken);
    Task<Connection?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Connection> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, OperationalStatus? status, string? afterCursor, int limit, CancellationToken cancellationToken);
    Task<ConnectionUsage> GetUsageAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<Connection?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string name,
        JsonElement config,
        SourceVerification? sourceVerification,
        DestinationAuthentication? destinationAuthentication,
        string? environment,
        string? description,
        CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}

public sealed record ConnectionUsage(bool Source, bool Destination);
