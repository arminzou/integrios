using System.Data.Common;
using Dapper;
using Integrios.Application.Topics;
using Integrios.Infrastructure.Data;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Topics;
using Npgsql;

namespace Integrios.Infrastructure.Topics;

internal sealed class TopicRepository(IDbConnectionFactory connectionFactory) : ITopicRepository
{
    private const string ForeignKeyViolation = "23503";
    private const string SourceConnectionTenantConstraint = "fk_topic_sources_connection_tenant";
    private const string UniqueViolation = "23505";

    private const string SelectColumns =
        "id AS Id, tenant_id AS TenantId, name AS Name, status AS Status, description AS Description, created_at AS CreatedAt, updated_at AS UpdatedAt";

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

        try
        {
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

            await InsertSourcesAsync(db, tenantId, id, sourceConnectionIds, tx, ct);
            var endpoints = await LoadSourceEndpointsAsync(db, id, tx, ct);
            await tx.CommitAsync(ct);

            return row.ToTopic(sourceConnectionIds.Distinct().ToList(), endpoints);
        }
        catch (NpgsqlException ex) when (ex.SqlState == UniqueViolation)
        {
            throw new DuplicateResourceException($"A topic named '{name}' already exists for this tenant.", ex);
        }
        catch (PostgresException ex) when (
            ex.SqlState == ForeignKeyViolation
            && ex.ConstraintName == SourceConnectionTenantConstraint)
        {
            throw new TopicRequestValidationException(
                "Every source connection must exist in the same tenant as the topic.", ex);
        }
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

        var sources = await LoadSourcesAsync(db, id, null, ct);
        var endpoints = await LoadSourceEndpointsAsync(db, id, null, ct);
        return row.ToTopic(sources, endpoints);
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
        int fetchLimit = limit + 1;

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
                new { TenantId = tenantId, CursorTime = cursorTime, CursorId = cursorId, Limit = fetchLimit },
                cancellationToken: ct))).ToList();

        if (rows.Count == 0)
            return ([], null);

        bool hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var topicIds = rows.Select(r => r.Id).ToArray();
        var sourceMap = await LoadSourcesForTopicsAsync(db, topicIds, ct);
        var endpointMap = await LoadSourceEndpointsForTopicsAsync(db, topicIds, ct);

        var items = rows
            .Select(r => r.ToTopic(
                sourceMap.TryGetValue(r.Id, out var s) ? s : [],
                endpointMap.TryGetValue(r.Id, out var e) ? e : []))
            .ToList();

        var nextCursor = hasMore
            ? PageCursor.Encode(rows[^1].CreatedAt, rows[^1].Id)
            : null;

        return (items, nextCursor);
    }

    public async Task<Topic?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string? name,
        string? description,
        IReadOnlyList<Guid>? sourceConnectionIds,
        CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        try
        {
            var row = await db.QuerySingleOrDefaultAsync<TopicRow>(
                new CommandDefinition(
                    $"""
                    UPDATE topics
                    SET description = @Description, updated_at = now()
                    WHERE tenant_id = @TenantId AND id = @Id AND name = @Name AND status != 'disabled'
                    RETURNING {SelectColumns}
                    """,
                    new { TenantId = tenantId, Id = id, Name = name, Description = description },
                    tx,
                    cancellationToken: ct));

            if (row is null)
            {
                var existingName = await db.QuerySingleOrDefaultAsync<string?>(
                    new CommandDefinition(
                        "SELECT name FROM topics WHERE tenant_id = @TenantId AND id = @Id",
                        new { TenantId = tenantId, Id = id },
                        tx,
                        cancellationToken: ct));
                await tx.RollbackAsync(ct);

                if (existingName is not null && string.IsNullOrWhiteSpace(name))
                    throw new TopicRequestValidationException("Topic name is required for update.");

                if (existingName is not null && !string.Equals(existingName, name, StringComparison.Ordinal))
                    throw new TopicRequestValidationException(
                        "Topic names are immutable; create a new topic to change the stream identifier.");

                return null;
            }

            if (sourceConnectionIds is not null)
            {
                Guid[] desiredConnectionIds = sourceConnectionIds.Distinct().ToArray();
                await RetireRemovedSourcesAsync(db, tenantId, id, desiredConnectionIds, tx, ct);
                await InsertSourcesAsync(db, tenantId, id, desiredConnectionIds, tx, ct);
            }

            var sources = await LoadSourcesAsync(db, id, tx, ct);
            var endpoints = await LoadSourceEndpointsAsync(db, id, tx, ct);
            await tx.CommitAsync(ct);
            return row.ToTopic(sources, endpoints);
        }
        catch (PostgresException ex) when (
            ex.SqlState == ForeignKeyViolation
            && ex.ConstraintName == SourceConnectionTenantConstraint)
        {
            throw new TopicRequestValidationException(
                "Every source connection must exist in the same tenant as the topic.", ex);
        }
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);
        var affected = await db.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE topics
                SET status = 'disabled', updated_at = now()
                WHERE tenant_id = @TenantId AND id = @Id AND status != 'disabled'
                """,
                new { TenantId = tenantId, Id = id },
                cancellationToken: ct));
        return affected > 0;
    }

    private static async Task InsertSourcesAsync(
        DbConnection db,
        Guid tenantId,
        Guid topicId,
        IReadOnlyList<Guid> connectionIds,
        DbTransaction tx,
        CancellationToken ct)
    {
        foreach (var cid in connectionIds)
        {
            Guid endpointId = Guid.NewGuid();
            await db.ExecuteAsync(
                new CommandDefinition(
                    """
                    WITH activated AS (
                        INSERT INTO topic_sources (
                            tenant_id, topic_id, connection_id, status, retired_at)
                        VALUES (@TenantId, @TopicId, @ConnectionId, 'active', NULL)
                        ON CONFLICT (tenant_id, topic_id, connection_id) DO UPDATE
                        SET status = 'active', retired_at = NULL
                        WHERE topic_sources.status = 'retired'
                        RETURNING tenant_id, topic_id, connection_id
                    )
                    INSERT INTO source_endpoints (
                        id, tenant_id, topic_id, connection_id, callback_path, status)
                    SELECT
                        @EndpointId,
                        activated.tenant_id,
                        activated.topic_id,
                        activated.connection_id,
                        '/webhooks/' || integrations.key || '/' || @EndpointId::text,
                        'active'
                    FROM activated
                    JOIN connections
                      ON connections.tenant_id = activated.tenant_id
                     AND connections.id = activated.connection_id
                    JOIN integrations ON integrations.id = connections.integration_id
                    WHERE jsonb_typeof(integrations.manifest->'source_adapter') = 'object'
                    """,
                    new
                    {
                        TenantId = tenantId,
                        TopicId = topicId,
                        ConnectionId = cid,
                        EndpointId = endpointId,
                    },
                    tx,
                    cancellationToken: ct));
        }
    }

    private static async Task RetireRemovedSourcesAsync(
        DbConnection db,
        Guid tenantId,
        Guid topicId,
        Guid[] desiredConnectionIds,
        DbTransaction tx,
        CancellationToken ct)
    {
        var parameters = new
        {
            TenantId = tenantId,
            TopicId = topicId,
            ConnectionIds = desiredConnectionIds,
        };

        await db.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE source_endpoints
                SET status = 'retired', retired_at = now()
                WHERE tenant_id = @TenantId
                  AND topic_id = @TopicId
                  AND status = 'active'
                  AND NOT (connection_id = ANY(@ConnectionIds))
                """,
                parameters,
                tx,
                cancellationToken: ct));

        await db.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE topic_sources
                SET status = 'retired', retired_at = now()
                WHERE tenant_id = @TenantId
                  AND topic_id = @TopicId
                  AND status = 'active'
                  AND NOT (connection_id = ANY(@ConnectionIds))
                """,
                parameters,
                tx,
                cancellationToken: ct));
    }

    private static async Task<IReadOnlyList<Guid>> LoadSourcesAsync(
        DbConnection db,
        Guid topicId,
        DbTransaction? tx,
        CancellationToken ct)
    {
        var ids = await db.QueryAsync<Guid>(
            new CommandDefinition(
                "SELECT connection_id FROM topic_sources WHERE topic_id = @TopicId AND status = 'active' ORDER BY created_at",
                new { TopicId = topicId },
                tx,
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
                "SELECT topic_id AS TopicId, connection_id AS ConnectionId FROM topic_sources WHERE topic_id = ANY(@TopicIds) AND status = 'active' ORDER BY topic_id, created_at",
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

    private static async Task<IReadOnlyList<SourceEndpoint>> LoadSourceEndpointsAsync(
        DbConnection db,
        Guid topicId,
        DbTransaction? tx,
        CancellationToken ct)
    {
        var rows = await db.QueryAsync<SourceEndpointRow>(
            new CommandDefinition(
                """
                SELECT
                    id AS Id,
                    tenant_id AS TenantId,
                    topic_id AS TopicId,
                    connection_id AS ConnectionId,
                    callback_path AS CallbackPath,
                    created_at AS CreatedAt
                FROM source_endpoints
                WHERE topic_id = @TopicId
                  AND status = 'active'
                ORDER BY created_at, id
                """,
                new { TopicId = topicId },
                tx,
                cancellationToken: ct));
        return rows.Select(static row => row.ToSourceEndpoint()).ToList();
    }

    private static async Task<Dictionary<Guid, List<SourceEndpoint>>> LoadSourceEndpointsForTopicsAsync(
        DbConnection db,
        Guid[] topicIds,
        CancellationToken ct)
    {
        var rows = await db.QueryAsync<SourceEndpointRow>(
            new CommandDefinition(
                """
                SELECT
                    id AS Id,
                    tenant_id AS TenantId,
                    topic_id AS TopicId,
                    connection_id AS ConnectionId,
                    callback_path AS CallbackPath,
                    created_at AS CreatedAt
                FROM source_endpoints
                WHERE topic_id = ANY(@TopicIds)
                  AND status = 'active'
                ORDER BY topic_id, created_at, id
                """,
                new { TopicIds = topicIds },
                cancellationToken: ct));

        var map = new Dictionary<Guid, List<SourceEndpoint>>();
        foreach (SourceEndpointRow row in rows)
        {
            if (!map.TryGetValue(row.TopicId, out List<SourceEndpoint>? endpoints))
                map[row.TopicId] = endpoints = [];
            endpoints.Add(row.ToSourceEndpoint());
        }
        return map;
    }

    private sealed record SourceRow
    {
        public Guid TopicId { get; init; }
        public Guid ConnectionId { get; init; }
    }

    private sealed record SourceEndpointRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public Guid TopicId { get; init; }
        public Guid ConnectionId { get; init; }
        public string CallbackPath { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }

        public SourceEndpoint ToSourceEndpoint() => new()
        {
            Id = Id,
            TenantId = TenantId,
            TopicId = TopicId,
            ConnectionId = ConnectionId,
            CallbackPath = CallbackPath,
            CreatedAt = CreatedAt,
        };
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

        public Topic ToTopic(
            IReadOnlyList<Guid> sourceConnectionIds,
            IReadOnlyList<SourceEndpoint> sourceEndpoints) => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            SourceConnectionIds = sourceConnectionIds,
            SourceEndpoints = sourceEndpoints,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
