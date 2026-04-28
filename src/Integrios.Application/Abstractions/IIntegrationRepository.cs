using Integrios.Domain.Integrations;

namespace Integrios.Application.Abstractions;

public interface IIntegrationRepository
{
    Task<Integration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Integration> Items, string? NextCursor)> ListAsync(string? afterCursor, int limit, CancellationToken cancellationToken = default);
}
