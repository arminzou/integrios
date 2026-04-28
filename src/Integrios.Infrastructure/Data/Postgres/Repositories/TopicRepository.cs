using System.Data.Common;
using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Topics;

namespace Integrios.Infrastructure.Data;

public sealed class TopicRepository(IDbConnectionFactory connectionFactory) : ITopicRepository
{
    private const string SelectColumns = "id, tenant_id, name, status, description, created_at, updated_at";

    public async Task<Topic> CreateAsync(
        Guid tenantId,
        string name,
        string? description,
        IReadOnlyList<Guid> sourceConnectionIds,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        var row = await db.QuerySingleAsync<TopicRow>(
            new CommandDefinition(
                $"""
                INSERT INTO topics (id, tenant_id, name, status, description, created_at, updated_at)
                VALUES (@Id, @TenantId, @Name, 'active', @Description, @Now, @Now)
                RETURNING {SelectColumns}
                """,
                new { Id = id, TenantId = tenantId, Name = name, Description = description, Now = now },
                tx,
                cancellationToken: ct));

        await InsertSourcesAsync(db, id, sourceConnectionIds, tx, ct);
        await tx.CommitAsync(ct);

        return row.ToTopic(sourceConnectionIds);
    }

    public async Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);

        var row = await db.QuerySingleOrDefaultAsync<TopicRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM topics WHERE tenant_id = @TenantId AND id = @Id LIMIT 1",
                new { TenantId = tenantId, Id = id },
                cancellationToken: ct));

        if (row is null)
            return null;

        var sources = await LoadSourcesAsync(db, id, ct);
        return row.ToTopic(sources);
    }

    public async Task<(IReadOnlyList<Topic> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        string? afterCursor,
        int limit,
        CancellationToken ct = default)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        var hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorTime, out cursorId);

        var sql = hasCursor
            ? $"""
               SELECT {SelectColumns} FROM topics
               WHERE tenant_id = @TenantId AND (created_at, id) > (@CursorTime, @CursorId)
               ORDER BY created_at, id LIMIT @Limit
               """
            : $"""
               SELECT {SelectColumns} FROM topics
               WHERE tenant_id = @TenantId
               ORDER BY created_at, id LIMIT @Limit
               """;

        await using var db = await connectionFactory.OpenConnectionAsync(ct);
        var rows = (await db.QueryAsync<TopicRow>(
            new CommandDefinition(
                sql,
                new { TenantId = tenantId, CursorTime = cursorTime, CursorId = cursorId, Limit = limit },
                cancellationToken: ct))).ToList();

        if (rows.Count == 0)
            return ([], null);

        var topicIds = rows.Select(r => r.Id).ToArray();
        var sourceMap = await LoadSourcesForTopicsAsync(db, topicIds, ct);

        var items = rows
            .Select(r => r.ToTopic(sourceMap.TryGetValue(r.Id, out var s) ? s : []))
            .ToList();

        var nextCursor = rows.Count == limit
            ? PageCursor.Encode(rows[^1].CreatedAt, rows[^1].Id)
            : null;

        return (items, nextCursor);
    }

    public async Task<Topic?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string name,
        string? description,
        CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);

        var row = await db.QuerySingleOrDefaultAsync<TopicRow>(
            new CommandDefinition(
                $"""
                UPDATE topics
                SET name = @Name, description = @Description, updated_at = now()
                WHERE tenant_id = @TenantId AND id = @Id AND status != 'inactive'
                RETURNING {SelectColumns}
                """,
                new { TenantId = tenantId, Id = id, Name = name, Description = description },
                cancellationToken: ct));

        if (row is null)
            return null;

        var sources = await LoadSourcesAsync(db, id, ct);
        return row.ToTopic(sources);
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);
        var affected = await db.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE topics
                SET status = 'inactive', updated_at = now()
                WHERE tenant_id = @TenantId AND id = @Id AND status != 'inactive'
                """,
                new { TenantId = tenantId, Id = id },
                cancellationToken: ct));
        return affected > 0;
    }

    public async Task<bool> SetSourceConnectionsAsync(
        Guid tenantId,
        Guid id,
        IReadOnlyList<Guid> sourceConnectionIds,
        CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        var exists = await db.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM topics WHERE tenant_id = @TenantId AND id = @Id)",
                new { TenantId = tenantId, Id = id },
                tx,
                cancellationToken: ct));

        if (!exists)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await db.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM topic_sources WHERE topic_id = @TopicId",
                new { TopicId = id },
                tx,
                cancellationToken: ct));

        await InsertSourcesAsync(db, id, sourceConnectionIds, tx, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<Guid?> FindByNameAsync(Guid tenantId, string name, CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);
        return await db.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT id FROM topics WHERE tenant_id = @TenantId AND name = @Name AND status = 'active' LIMIT 1",
                new { TenantId = tenantId, Name = name },
                cancellationToken: ct));
    }

    private static async Task InsertSourcesAsync(
        DbConnection db,
        Guid topicId,
        IReadOnlyList<Guid> connectionIds,
        DbTransaction tx,
        CancellationToken ct)
    {
        foreach (var cid in connectionIds)
        {
            await db.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO topic_sources (topic_id, connection_id) VALUES (@TopicId, @ConnectionId) ON CONFLICT DO NOTHING",
                    new { TopicId = topicId, ConnectionId = cid },
                    tx,
                    cancellationToken: ct));
        }
    }

    private static async Task<IReadOnlyList<Guid>> LoadSourcesAsync(DbConnection db, Guid topicId, CancellationToken ct)
    {
        var ids = await db.QueryAsync<Guid>(
            new CommandDefinition(
                "SELECT connection_id FROM topic_sources WHERE topic_id = @TopicId ORDER BY created_at",
                new { TopicId = topicId },
                cancellationToken: ct));
        return ids.ToList();
    }

    private static async Task<Dictionary<Guid, List<Guid>>> LoadSourcesForTopicsAsync(
        DbConnection db,
        Guid[] topicIds,
        CancellationToken ct)
    {
        var rows = await db.QueryAsync<SourceRow>(
            new CommandDefinition(
                "SELECT topic_id AS TopicId, connection_id AS ConnectionId FROM topic_sources WHERE topic_id = ANY(@TopicIds) ORDER BY topic_id, created_at",
                new { TopicIds = topicIds },
                cancellationToken: ct));

        var map = new Dictionary<Guid, List<Guid>>();
        foreach (var row in rows)
        {
            if (!map.TryGetValue(row.TopicId, out var list))
                map[row.TopicId] = list = [];
            list.Add(row.ConnectionId);
        }
        return map;
    }

    private sealed record SourceRow
    {
        public Guid TopicId { get; init; }
        public Guid ConnectionId { get; init; }
    }

    private sealed record TopicRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Topic ToTopic(IReadOnlyList<Guid> sourceConnectionIds) => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            SourceConnectionIds = sourceConnectionIds,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
