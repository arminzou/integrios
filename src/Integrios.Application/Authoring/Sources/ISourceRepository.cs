using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Sources;

public interface ISourceRepository
{
    Task<Source> CreateAsync(Source source, CancellationToken cancellationToken);
    Task<Source?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<Source> Items, string? NextCursor)> ListByTenantAsync(Guid tenantId, SourceStatus? status, SourceType? type, string? afterCursor, int limit, CancellationToken cancellationToken);
    Task<Source?> UpdateAsync(Guid tenantId, Guid id, JsonElement configuration, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}
