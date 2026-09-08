using System.Text.Json;
using Integrios.Application.Authoring.Sources;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Sources;

internal sealed class SourceRepository(IntegriosDbContext context) : ISourceRepository
{
    public async Task<Source> CreateAsync(Source source, CancellationToken cancellationToken)
    {
        context.Sources.Add(source);
        await context.SaveChangesAsync(cancellationToken);
        return source;
    }

    public Task<Source?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.Sources.AsNoTracking().SingleOrDefaultAsync(source => source.TenantId == tenantId && source.Id == id, cancellationToken);

    public async Task<Source?> UpdateAsync(Guid tenantId, Guid id, JsonElement configuration, CancellationToken cancellationToken)
    {
        int affected = await context.Sources.Where(source => source.TenantId == tenantId && source.Id == id && source.Status == SourceStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Configuration, configuration)
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        return affected == 0 ? null : await GetByIdAsync(tenantId, id, cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await context.Sources.Where(source => source.TenantId == tenantId && source.Id == id && source.Status == SourceStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, SourceStatus.Revoked)
                .SetProperty(source => source.RevokedAt, DateTimeOffset.UtcNow)
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) > 0;
}
