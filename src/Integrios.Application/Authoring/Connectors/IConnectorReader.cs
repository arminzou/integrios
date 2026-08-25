using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connectors;

public interface IConnectorReader
{
    Task<Connector?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<Connector> Items, string? NextCursor)> ListAsync(
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken);
}
