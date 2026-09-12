using Integrios.Application.Authoring.Topics;
using Integrios.Infrastructure.Data;
using Integrios.Application.Common.Exceptions;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Topics;

internal sealed class TopicRepository(IntegriosDbContext context, IDataProtectionProvider dataProtectionProvider) : ITopicRepository
{
    public async Task<Topic> CreateAsync(
        Guid tenantId,
        string key,
        string name,
        string? description,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var topic = new Topic
        {
            Id = id,
            TenantId = tenantId,
            Key = key,
            Name = name,
            Status = OperationalStatus.Active,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            context.Topics.Add(topic);
            await context.SaveChangesAsync(ct);
            return topic;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateResourceException($"A topic keyed '{key}' already exists for this tenant.", ex);
        }
    }

    public async Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        Topic? topic = await context.Topics.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Id == id,
            ct);
        if (topic is null)
            return null;

        return topic;
    }

    public Task<int> CountSubscriptionsAsync(Guid tenantId, Guid topicId, CancellationToken ct) =>
        context.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.TenantId == tenantId && subscription.TopicId == topicId)
            .CountAsync(ct);

    public async Task<(IReadOnlyList<TopicListRow> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        TopicListFilter filter,
        string? afterCursor,
        int limit,
        CancellationToken ct)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        // Both filters reach the scope; a cursor is only valid for the scope that issued it.
        string cursorScope = string.Join(
            ':',
            "topics",
            tenantId.ToString("N"),
            filter.Status?.ToString() ?? "all",
            filter.NameContains ?? "all");
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorTime, out cursorId))
            throw new InvalidCursorException();

        IQueryable<Topic> query = context.Topics.AsNoTracking().Where(topic => topic.TenantId == tenantId);
        if (filter.Status is not null)
            query = query.Where(topic => topic.Status == filter.Status);
        // Lowered on both sides so the match does not depend on the provider's collation.
        if (filter.NameContains is not null)
        {
            string lowered = filter.NameContains.ToLowerInvariant();
            query = query.Where(topic => topic.Name.ToLower().Contains(lowered));
        }
        if (hasCursor)
        {
            query = query.Where(topic =>
                topic.CreatedAt < cursorTime
                || (topic.CreatedAt == cursorTime && topic.Id.CompareTo(cursorId) < 0));
        }

        // Counted in the same statement rather than per row: a Topics list is read to see which
        // Topics nothing subscribes to, so the count is the column, not a follow-up.
        var page = await query
            .OrderByDescending(topic => topic.CreatedAt)
            .ThenByDescending(topic => topic.Id)
            .Take(limit + 1)
            .Select(topic => new
            {
                Topic = topic,
                SubscriptionCount = context.Subscriptions
                    .Count(subscription => subscription.TenantId == tenantId && subscription.TopicId == topic.Id),
            })
            .ToListAsync(ct);

        bool hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        List<TopicListRow> items = page
            .Select(row => new TopicListRow(row.Topic, row.SubscriptionCount))
            .ToList();

        var nextCursor = hasMore
            ? PageCursor.Encode(dataProtectionProvider, cursorScope, items[^1].Topic.CreatedAt, items[^1].Topic.Id, DateTimeOffset.UtcNow)
            : null;

        return (items, nextCursor);
    }

    public async Task<Topic?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string? name,
        string? description,
        CancellationToken ct)
    {
        Topic? existing = await context.Topics.AsNoTracking().SingleOrDefaultAsync(
            topic => topic.TenantId == tenantId && topic.Id == id,
            ct);
        if (existing is null)
            return null;
        if (string.IsNullOrWhiteSpace(name))
            throw new TopicValidationException("Topic name is required for update.");
        if (existing.Status == OperationalStatus.Disabled)
            return null;

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        await context.Topics
            .Where(topic => topic.TenantId == tenantId && topic.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(topic => topic.Name, name)
                    .SetProperty(topic => topic.Description, description)
                    .SetProperty(topic => topic.UpdatedAt, updatedAt),
                ct);

        return existing with { Name = name, Description = description, UpdatedAt = updatedAt };
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct)
        => await context.Topics
            .Where(topic =>
                topic.TenantId == tenantId
                && topic.Id == id
                && topic.Status != OperationalStatus.Disabled)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(topic => topic.Status, OperationalStatus.Disabled)
                    .SetProperty(topic => topic.UpdatedAt, DateTimeOffset.UtcNow),
                ct) > 0;

}
