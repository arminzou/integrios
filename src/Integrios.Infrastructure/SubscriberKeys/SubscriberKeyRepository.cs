using Integrios.Application.Authoring.SubscriberKeys;
using Integrios.Application.Common.Exceptions;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.SubscriberKeys;

internal sealed class SubscriberKeyRepository(IntegriosDbContext context, IDataProtectionProvider dataProtectionProvider) : ISubscriberKeyRepository
{
    public async Task<SubscriberKey> CreateAsync(SubscriberKey subscriberKey, CancellationToken cancellationToken)
    {
        context.SubscriberKeys.Add(subscriberKey);
        await context.SaveChangesAsync(cancellationToken);
        return subscriberKey;
    }

    public Task<SubscriberKey?> GetByIdAsync(Guid tenantId, Guid destinationId, Guid id, CancellationToken cancellationToken) =>
        context.SubscriberKeys.AsNoTracking().SingleOrDefaultAsync(
            subscriberKey => subscriberKey.TenantId == tenantId && subscriberKey.DestinationId == destinationId && subscriberKey.Id == id,
            cancellationToken);

    public async Task<(IReadOnlyList<SubscriberKey> Items, string? NextCursor)> ListByDestinationAsync(
        Guid tenantId, Guid destinationId,
        SubscriberKeyListState? state,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        string cursorScope = $"subscriber-keys:{tenantId:N}:{destinationId:N}:{state?.ToString() ?? "all"}";
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorCreatedAt, out cursorId))
            throw new InvalidCursorException();

        IQueryable<SubscriberKey> query = context.SubscriberKeys.AsNoTracking()
            .Where(subscriberKey => subscriberKey.TenantId == tenantId && subscriberKey.DestinationId == destinationId);
        query = state switch
        {
            SubscriberKeyListState.Active => query.Where(subscriberKey => subscriberKey.RevokedAt == null),
            SubscriberKeyListState.Revoked => query.Where(subscriberKey => subscriberKey.RevokedAt != null),
            _ => query,
        };
        if (hasCursor)
        {
            query = query.Where(subscriberKey =>
                subscriberKey.CreatedAt < cursorCreatedAt
                || (subscriberKey.CreatedAt == cursorCreatedAt && subscriberKey.Id.CompareTo(cursorId) < 0));
        }

        List<SubscriberKey> items = await query
            .OrderByDescending(subscriberKey => subscriberKey.CreatedAt)
            .ThenByDescending(subscriberKey => subscriberKey.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            nextCursor = PageCursor.Encode(dataProtectionProvider, cursorScope, items[^1].CreatedAt, items[^1].Id, DateTimeOffset.UtcNow);
        }

        return (items, nextCursor);
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid destinationId, Guid id, CancellationToken cancellationToken) =>
        await context.SubscriberKeys
            .Where(subscriberKey =>
                subscriberKey.TenantId == tenantId && subscriberKey.DestinationId == destinationId
                && subscriberKey.Id == id
                && subscriberKey.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(subscriberKey => subscriberKey.RevokedAt, DateTimeOffset.UtcNow),
                cancellationToken) > 0;

    public async Task<bool> DeleteAsync(Guid tenantId, Guid destinationId, Guid id, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string deletedHash = $"deleted:{id:N}";
        return await context.SubscriberKeys
            .Where(subscriberKey =>
                subscriberKey.TenantId == tenantId && subscriberKey.DestinationId == destinationId
                && subscriberKey.Id == id
                && subscriberKey.RevokedAt != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(subscriberKey => subscriberKey.KeyHash, deletedHash)
                    .SetProperty(subscriberKey => subscriberKey.DeletedAt, now),
                cancellationToken) > 0;
    }
}
