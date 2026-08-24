using Integrios.Application.Topics;
using Integrios.Infrastructure.Data;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Integrios.Infrastructure.Topics;

internal sealed class TopicRepository(IntegriosDbContext context) : ITopicRepository
{
    public async Task<Topic> CreateAsync(
        Guid tenantId,
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
            throw new DuplicateResourceException($"A topic named '{name}' already exists for this tenant.", ex);
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

    public async Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        string? afterCursor,
        int limit,
        CancellationToken ct)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null
            && PageCursor.TryDecode(afterCursor, out cursorTime, out cursorId);

        IQueryable<Topic> query = context.Topics.AsNoTracking().Where(topic => topic.TenantId == tenantId);
        if (hasCursor)
        {
            query = query.Where(topic =>
                topic.CreatedAt > cursorTime
                || (topic.CreatedAt == cursorTime && topic.Id.CompareTo(cursorId) > 0));
        }

        List<Topic> topics = await query
            .OrderBy(topic => topic.CreatedAt)
            .ThenBy(topic => topic.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        bool hasMore = topics.Count > limit;
        if (hasMore)
            topics.RemoveAt(topics.Count - 1);

        List<Topic> items = topics;

        var nextCursor = hasMore
            ? PageCursor.Encode(topics[^1].CreatedAt, topics[^1].Id)
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
        if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
        {
            throw new TopicValidationException(
                "Topic names are immutable; create a new topic to change the stream identifier.");
        }
        if (existing.Status == OperationalStatus.Disabled)
            return null;

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        await context.Topics
            .Where(topic => topic.TenantId == tenantId && topic.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(topic => topic.Description, description)
                    .SetProperty(topic => topic.UpdatedAt, updatedAt),
                ct);

        return existing with { Description = description, UpdatedAt = updatedAt };
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
