using Integrios.Domain.Connectors;

namespace Integrios.Application.Connectors;

public interface IConnectorCatalog
{
    Task<Connector?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<Connector> Items, string? NextCursor)> ListAsync(
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken);
}
