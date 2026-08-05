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

            var sources = await UpsertSourcesAsync(db, tenantId, id, sourceConnectionIds.Distinct().ToArray(), tx, ct);
            await tx.CommitAsync(ct);

            return row.ToTopic(sources);
        }
        catch (NpgsqlException ex) when (ex.SqlState == UniqueViolation)
        {
            throw new DuplicateResourceException($"A topic named '{name}' already exists for this tenant.", ex);
        }
        catch (PostgresException ex) when (
            ex.SqlState == ForeignKeyViolation
            && ex.ConstraintName == SourceConnectionTenantConstraint)
        {
            throw new TopicValidationException(
                "Every source connection must exist in the same tenant as the topic.", ex);
        }
    }

    public async Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var db = await connectionFactory.OpenConnectionAsync(ct);

        var rows = (await db.QueryAsync<TopicWithSourceRow>(
            new CommandDefinition(
                """
                SELECT
                    t.id AS Id,
                    t.tenant_id AS TenantId,
                    t.name AS Name,
                    t.status AS Status,
                    t.description AS Description,
                    t.created_at AS CreatedAt,
                    t.updated_at AS UpdatedAt,
                    ts.connection_id AS SourceConnectionId,
                    ts.created_at AS SourceCreatedAt,
                    se.id AS EndpointId,
                    se.callback_path AS CallbackPath,
                    se.created_at AS EndpointCreatedAt
                FROM topics t
                LEFT JOIN topic_sources ts
                    ON ts.tenant_id = t.tenant_id
                   AND ts.topic_id = t.id
                   AND ts.status = 'active'
                -- Keep the endpoint status predicate in the LEFT JOIN. Moving it to WHERE drops
                -- endpoint-free sources by turning the outer join into an inner join.
                LEFT JOIN source_endpoints se
                    ON se.tenant_id = ts.tenant_id
                   AND se.topic_id = ts.topic_id
                   AND se.connection_id = ts.connection_id
                   AND se.status = 'active'
                WHERE t.tenant_id = @TenantId AND t.id = @Id
                ORDER BY ts.created_at, ts.connection_id
                """,
                new { TenantId = tenantId, Id = id },
                cancellationToken: ct))).ToList();

        if (rows.Count == 0)
            return null;

        var sources = rows
            .Where(static row => row.SourceConnectionId is not null)
            .Select(static row => row.ToTopicSource())
            .ToList();
        return rows[0].ToTopic(sources);
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
        var sourceMap = await LoadSourcesForTopicsAsync(db, tenantId, topicIds, ct);

        var items = rows
            .Select(r => r.ToTopic(sourceMap.TryGetValue(r.Id, out var s) ? s : []))
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
            if (sourceConnectionIds is null)
            {
                var rows = (await db.QueryAsync<TopicWithSourceRow>(
                    new CommandDefinition(
                        """
                        WITH updated AS (
                            UPDATE topics
                            SET description = @Description, updated_at = now()
                            WHERE tenant_id = @TenantId AND id = @Id AND name = @Name AND status != 'disabled'
                            RETURNING id, tenant_id, name, status, description, created_at, updated_at
                        )
                        SELECT
                            u.id AS Id,
                            u.tenant_id AS TenantId,
                            u.name AS Name,
                            u.status AS Status,
                            u.description AS Description,
                            u.created_at AS CreatedAt,
                            u.updated_at AS UpdatedAt,
                            ts.connection_id AS SourceConnectionId,
                            ts.created_at AS SourceCreatedAt,
                            se.id AS EndpointId,
                            se.callback_path AS CallbackPath,
                            se.created_at AS EndpointCreatedAt
                        FROM updated u
                        LEFT JOIN topic_sources ts
                            ON ts.tenant_id = u.tenant_id
                           AND ts.topic_id = u.id
                           AND ts.status = 'active'
                        -- Keep the endpoint status predicate in the LEFT JOIN. Moving it to WHERE drops
                        -- endpoint-free sources by turning the outer join into an inner join.
                        LEFT JOIN source_endpoints se
                            ON se.tenant_id = ts.tenant_id
                           AND se.topic_id = ts.topic_id
                           AND se.connection_id = ts.connection_id
                           AND se.status = 'active'
                        ORDER BY ts.created_at, ts.connection_id
                        """,
                        new { TenantId = tenantId, Id = id, Name = name, Description = description },
                        tx,
                        cancellationToken: ct))).ToList();

                if (rows.Count > 0)
                {
                    var unchangedSources = rows
                        .Where(static row => row.SourceConnectionId is not null)
                        .Select(static row => row.ToTopicSource())
                        .ToList();
                    await tx.CommitAsync(ct);
                    return rows[0].ToTopic(unchangedSources);
                }

                return await ResolveMissingUpdateAsync(db, tenantId, id, name, tx, ct);
            }

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
                return await ResolveMissingUpdateAsync(db, tenantId, id, name, tx, ct);

            var desired = sourceConnectionIds.Distinct().ToArray();
            await RemoveSourcesNotInAsync(db, tenantId, id, desired, tx, ct);
            IReadOnlyList<TopicSource> sources = await UpsertSourcesAsync(db, tenantId, id, desired, tx, ct);

            await tx.CommitAsync(ct);
            return row.ToTopic(sources);
        }
        catch (PostgresException ex) when (
            ex.SqlState == ForeignKeyViolation
            && ex.ConstraintName == SourceConnectionTenantConstraint)
        {
            throw new TopicValidationException(
                "Every source connection must exist in the same tenant as the topic.", ex);
        }
    }

    private static async Task<Topic?> ResolveMissingUpdateAsync(
        DbConnection db,
        Guid tenantId,
        Guid id,
        string? requestedName,
        DbTransaction tx,
        CancellationToken ct)
    {
        var existingName = await db.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT name FROM topics WHERE tenant_id = @TenantId AND id = @Id",
                new { TenantId = tenantId, Id = id },
                tx,
                cancellationToken: ct));
        await tx.RollbackAsync(ct);

        if (existingName is not null && string.IsNullOrWhiteSpace(requestedName))
            throw new TopicValidationException("Topic name is required for update.");

        if (existingName is not null && !string.Equals(existingName, requestedName, StringComparison.Ordinal))
        {
            throw new TopicValidationException(
                "Topic names are immutable; create a new topic to change the stream identifier.");
        }

        return null;
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

    // Activates (or reactivates) each source Connection and returns its correlated TopicSource,
    // including its source endpoint when one already exists or is newly minted here. One
    // statement per connection id, and none of them need a follow-up read.
    private static async Task<IReadOnlyList<TopicSource>> UpsertSourcesAsync(
        DbConnection db,
        Guid tenantId,
        Guid topicId,
        IReadOnlyList<Guid> connectionIds,
        DbTransaction tx,
        CancellationToken ct)
    {
        var sources = new List<TopicSource>(connectionIds.Count);
        foreach (var connectionId in connectionIds)
        {
            var write = await db.QuerySingleAsync<SourceWriteRow>(
                new CommandDefinition(
                    """
                    WITH activated AS (
                        INSERT INTO topic_sources (tenant_id, topic_id, connection_id, status, inactive_at)
                        VALUES (@TenantId, @TopicId, @ConnectionId, 'active', NULL)
                        ON CONFLICT (tenant_id, topic_id, connection_id) DO UPDATE
                            SET status = 'active', inactive_at = NULL
                        RETURNING created_at
                    ),
                    existing_endpoint AS (
                        SELECT id, callback_path, created_at
                        FROM source_endpoints
                        WHERE tenant_id = @TenantId AND topic_id = @TopicId AND connection_id = @ConnectionId
                          AND status = 'active'
                    ),
                    adapter_eligible AS (
                        SELECT gen_random_uuid() AS endpoint_id, i.key AS integration_key
                        FROM connections c
                        JOIN integrations i ON i.id = c.integration_id
                        WHERE c.tenant_id = @TenantId AND c.id = @ConnectionId
                          AND jsonb_typeof(i.manifest -> 'source_adapter') = 'object'
                          AND NOT EXISTS (SELECT 1 FROM existing_endpoint)
                    ),
                    inserted_endpoint AS (
                        INSERT INTO source_endpoints (id, tenant_id, topic_id, connection_id, callback_path, status)
                        SELECT endpoint_id, @TenantId, @TopicId, @ConnectionId,
                               '/webhooks/' || integration_key || '/' || endpoint_id::text, 'active'
                        FROM adapter_eligible
                        RETURNING id, callback_path, created_at
                    )
                    SELECT
                        a.created_at AS SourceCreatedAt,
                        COALESCE(ie.id, ee.id) AS EndpointId,
                        COALESCE(ie.callback_path, ee.callback_path) AS CallbackPath,
                        COALESCE(ie.created_at, ee.created_at) AS EndpointCreatedAt
                    FROM activated a
                    LEFT JOIN inserted_endpoint ie ON true
                    LEFT JOIN existing_endpoint ee ON true
                    """,
                    new { TenantId = tenantId, TopicId = topicId, ConnectionId = connectionId },
                    tx,
                    cancellationToken: ct));

            sources.Add(write.ToTopicSource(connectionId));
        }

        return sources
            .OrderBy(static source => source.CreatedAt)
            .ThenBy(static source => source.ConnectionId)
            .ToList();
    }

    private static async Task RemoveSourcesNotInAsync(
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
                SET status = 'inactive', inactive_at = now()
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
                SET status = 'inactive', inactive_at = now()
                WHERE tenant_id = @TenantId
                  AND topic_id = @TopicId
                  AND status = 'active'
                  AND NOT (connection_id = ANY(@ConnectionIds))
                """,
                parameters,
                tx,
                cancellationToken: ct));
    }

    private static async Task<Dictionary<Guid, List<TopicSource>>> LoadSourcesForTopicsAsync(
        DbConnection db,
        Guid tenantId,
        Guid[] topicIds,
        CancellationToken ct)
    {
        var rows = await db.QueryAsync<TopicSourceWithTopicRow>(
            new CommandDefinition(
                """
                SELECT
                    ts.topic_id AS TopicId,
                    ts.connection_id AS ConnectionId,
                    ts.created_at AS SourceCreatedAt,
                    se.id AS EndpointId,
                    se.callback_path AS CallbackPath,
                    se.created_at AS EndpointCreatedAt
                FROM topic_sources ts
                -- Keep the endpoint status predicate in the LEFT JOIN. Moving it to WHERE drops
                -- endpoint-free sources by turning the outer join into an inner join.
                LEFT JOIN source_endpoints se
                    ON se.tenant_id = ts.tenant_id
                   AND se.topic_id = ts.topic_id
                   AND se.connection_id = ts.connection_id
                   AND se.status = 'active'
                WHERE ts.tenant_id = @TenantId AND ts.topic_id = ANY(@TopicIds) AND ts.status = 'active'
                ORDER BY ts.topic_id, ts.created_at, ts.connection_id
                """,
                new { TenantId = tenantId, TopicIds = topicIds },
                cancellationToken: ct));

        var map = new Dictionary<Guid, List<TopicSource>>();
        foreach (var row in rows)
        {
            if (!map.TryGetValue(row.TopicId, out var list))
                map[row.TopicId] = list = [];
            list.Add(row.ToTopicSource());
        }
        return map;
    }

    private sealed record SourceWriteRow
    {
        public DateTimeOffset SourceCreatedAt { get; init; }
        public Guid? EndpointId { get; init; }
        public string? CallbackPath { get; init; }
        public DateTimeOffset? EndpointCreatedAt { get; init; }

        public TopicSource ToTopicSource(Guid connectionId) => new()
        {
            ConnectionId = connectionId,
            CreatedAt = SourceCreatedAt,
            Endpoint = EndpointId is null
                ? null
                : new SourceEndpoint { Id = EndpointId.Value, CallbackPath = CallbackPath!, CreatedAt = EndpointCreatedAt!.Value },
        };
    }

    private sealed record TopicSourceWithTopicRow
    {
        public Guid TopicId { get; init; }
        public Guid ConnectionId { get; init; }
        public DateTimeOffset SourceCreatedAt { get; init; }
        public Guid? EndpointId { get; init; }
        public string? CallbackPath { get; init; }
        public DateTimeOffset? EndpointCreatedAt { get; init; }

        public TopicSource ToTopicSource() => new()
        {
            ConnectionId = ConnectionId,
            CreatedAt = SourceCreatedAt,
            Endpoint = EndpointId is null
                ? null
                : new SourceEndpoint { Id = EndpointId.Value, CallbackPath = CallbackPath!, CreatedAt = EndpointCreatedAt!.Value },
        };
    }

    private sealed record TopicWithSourceRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public Guid? SourceConnectionId { get; init; }
        public DateTimeOffset? SourceCreatedAt { get; init; }
        public Guid? EndpointId { get; init; }
        public string? CallbackPath { get; init; }
        public DateTimeOffset? EndpointCreatedAt { get; init; }

        public TopicSource ToTopicSource() => new()
        {
            ConnectionId = SourceConnectionId!.Value,
            CreatedAt = SourceCreatedAt!.Value,
            Endpoint = EndpointId is null
                ? null
                : new SourceEndpoint { Id = EndpointId.Value, CallbackPath = CallbackPath!, CreatedAt = EndpointCreatedAt!.Value },
        };

        public Topic ToTopic(IReadOnlyList<TopicSource> sources) => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            Sources = sources,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
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

        public Topic ToTopic(IReadOnlyList<TopicSource> sources) => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            Sources = sources,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
