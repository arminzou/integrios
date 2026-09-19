using System.Text.Json;
using Integrios.Application.Authoring.Sources;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
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

    public async Task<Source?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string name,
        JsonElement configuration,
        SourceVerification? verification,
        JsonElement? inputRequirements,
        SourceMapping? mapping,
        SourceEventIdentityRule? eventIdentityRule,
        IReadOnlyList<string> eventTypes,
        CancellationToken cancellationToken)
    {
        int affected = await context.Sources.Where(source => source.TenantId == tenantId && source.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Name, name)
                .SetProperty(source => source.Configuration, configuration)
                .SetProperty(source => source.Verification, verification)
                .SetProperty(source => source.InputRequirements, inputRequirements)
                .SetProperty(source => source.Mapping, mapping)
                .SetProperty(source => source.EventIdentityRule, eventIdentityRule)
                .SetProperty(source => source.EventTypes, eventTypes)
                .SetProperty(source => source.Revision, Guid.NewGuid().ToString("N"))
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        return affected == 0 ? null : await GetByIdAsync(tenantId, id, cancellationToken);
    }

    public async Task<bool> SetStatusAsync(Guid tenantId, Guid id, EnablementStatus status, CancellationToken cancellationToken) =>
        await context.Sources.Where(source => source.TenantId == tenantId && source.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.Status, status)
                .SetProperty(source => source.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) > 0;

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        JsonElement empty = JsonSerializer.Deserialize<JsonElement>("{}");
        return await context.Sources
            .Where(source => source.TenantId == tenantId && source.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(source => source.EventTypes, Array.Empty<string>())
                .SetProperty(source => source.Configuration, empty)
                .SetProperty(source => source.Verification, (SourceVerification?)null)
                .SetProperty(source => source.InputRequirements, (JsonElement?)null)
                .SetProperty(source => source.Mapping, (SourceMapping?)null)
                .SetProperty(source => source.EventIdentityRule, (SourceEventIdentityRule?)null)
                .SetProperty(source => source.Revision, string.Empty)
                .SetProperty(source => source.Status, EnablementStatus.Disabled)
                .SetProperty(source => source.DeletedAt, now)
                .SetProperty(source => source.UpdatedAt, now), cancellationToken) > 0;
    }
}
