using System.Data.Common;
using Dapper;
using Integrios.Application.Topics;
using Integrios.Infrastructure.Data;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Integrios.Infrastructure.Topics;

internal sealed class TopicRepository(IntegriosDbContext context) : ITopicRepository
{
    private const string ForeignKeyViolation = "23503";
    private const string SourceConnectionTenantConstraint = "fk_topic_sources_connection_tenant";

    public async Task<Topic> CreateAsync(
        Guid tenantId,
        string name,
        string? description,
        IReadOnlyList<Guid> sourceConnectionIds,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var topic = new Topic
        {
            Id = id,
            TenantId = tenantId,
            Name = name,
            Sources = [],
            Status = OperationalStatus.Active,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        try
        {
            context.Topics.Add(topic);
            await context.SaveChangesAsync(ct);

            DbConnection db = context.Database.GetDbConnection();
            IReadOnlyList<TopicSource> sources = await UpsertSourcesAsync(
                db,
                DatabaseProviders.FromContext(context.Database) == DatabaseProvider.SqlServer,
                tenantId,
                id,
                sourceConnectionIds.Distinct().ToArray(),
                transaction.GetDbTransaction(),
                ct);
            await transaction.CommitAsync(ct);

            return topic with { Sources = sources };
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException is SqlException { Number: 2601 or 2627 })
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
        catch (SqlException ex) when (ex.Number == 547 && ex.Message.Contains(SourceConnectionTenantConstraint, StringComparison.Ordinal))
        {
            throw new TopicValidationException(
                "Every source connection must exist in the same tenant as the topic.", ex);
        }
    }

    public async Task<Topic?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        Topic? topic = await context.Topics.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Id == id,
            ct);
        if (topic is null)
            return null;

        Dictionary<Guid, List<TopicSource>> sourceMap = await LoadSourcesForTopicsAsync(
            context.Database.GetDbConnection(),
            DatabaseProviders.FromContext(context.Database) == DatabaseProvider.SqlServer,
            tenantId,
            [id],
            ct);
        return topic with { Sources = sourceMap.GetValueOrDefault(id) ?? [] };
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

        Guid[] topicIds = topics.Select(topic => topic.Id).ToArray();
        Dictionary<Guid, List<TopicSource>> sourceMap = await LoadSourcesForTopicsAsync(
            context.Database.GetDbConnection(),
            DatabaseProviders.FromContext(context.Database) == DatabaseProvider.SqlServer,
            tenantId,
            topicIds,
            ct);

        List<Topic> items = topics
            .Select(topic => topic with { Sources = sourceMap.GetValueOrDefault(topic.Id) ?? [] })
            .ToList();

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
        IReadOnlyList<Guid>? sourceConnectionIds,
        CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        try
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

            DbConnection db = context.Database.GetDbConnection();
            DbTransaction dbTransaction = transaction.GetDbTransaction();
            IReadOnlyList<TopicSource> sources;
            if (sourceConnectionIds is null)
            {
                Dictionary<Guid, List<TopicSource>> sourceMap = await LoadSourcesForTopicsAsync(
                    db,
                    DatabaseProviders.FromContext(context.Database) == DatabaseProvider.SqlServer,
                    tenantId,
                    [id],
                    ct,
                    dbTransaction);
                sources = sourceMap.GetValueOrDefault(id) ?? [];
            }
            else
            {
                Guid[] desired = sourceConnectionIds.Distinct().ToArray();
                bool sqlServer = DatabaseProviders.FromContext(context.Database) == DatabaseProvider.SqlServer;
                await RemoveSourcesNotInAsync(db, sqlServer, tenantId, id, desired, dbTransaction, ct);
                sources = await UpsertSourcesAsync(db, sqlServer, tenantId, id, desired, dbTransaction, ct);
            }

            await transaction.CommitAsync(ct);
            return existing with { Description = description, UpdatedAt = updatedAt, Sources = sources };
        }
        catch (PostgresException ex) when (
            ex.SqlState == ForeignKeyViolation
            && ex.ConstraintName == SourceConnectionTenantConstraint)
        {
            throw new TopicValidationException(
                "Every source connection must exist in the same tenant as the topic.", ex);
        }
        catch (SqlException ex) when (ex.Number == 547 && ex.Message.Contains(SourceConnectionTenantConstraint, StringComparison.Ordinal))
        {
            throw new TopicValidationException(
                "Every source connection must exist in the same tenant as the topic.", ex);
        }
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

    // Activates (or reactivates) each source Connection and returns its correlated TopicSource,
    // including its source endpoint when one already exists or is newly minted here. One
    // statement per connection id, and none of them need a follow-up read.
    private static async Task<IReadOnlyList<TopicSource>> UpsertSourcesAsync(
        DbConnection db,
        bool sqlServer,
        Guid tenantId,
        Guid topicId,
        IReadOnlyList<Guid> connectionIds,
        DbTransaction tx,
        CancellationToken ct)
    {
        var sources = new List<TopicSource>(connectionIds.Count);
        foreach (var connectionId in connectionIds)
        {
            string sql = sqlServer
                ? """
                    DECLARE @activated table (created_at datetimeoffset);
                    MERGE topic_sources WITH (HOLDLOCK) AS target
                    USING (VALUES (@TenantId, @TopicId, @ConnectionId)) AS source(tenant_id, topic_id, connection_id)
                       ON target.tenant_id=source.tenant_id AND target.topic_id=source.topic_id
                      AND target.connection_id=source.connection_id
                    WHEN MATCHED THEN UPDATE SET status=N'active', inactive_at=NULL
                    WHEN NOT MATCHED THEN
                        INSERT (tenant_id, topic_id, connection_id, status, inactive_at)
                        VALUES (source.tenant_id, source.topic_id, source.connection_id, N'active', NULL)
                    OUTPUT inserted.created_at INTO @activated;

                    DECLARE @EndpointId uniqueidentifier;
                    DECLARE @CallbackPath nvarchar(450);
                    DECLARE @EndpointCreatedAt datetimeoffset;
                    SELECT @EndpointId=id, @CallbackPath=callback_path, @EndpointCreatedAt=created_at
                    FROM source_endpoints
                    WHERE tenant_id=@TenantId AND topic_id=@TopicId AND connection_id=@ConnectionId
                      AND status=N'active';
                    -- No Source entity exists yet to pick a source_contracts[] entry by key, so entry
                    -- 0 transitionally stands in for "the" Source. Only a *registered* (compiled)
                    -- contract has a runtime handler to serve a live webhook; the manifest parser
                    -- guarantees a registered entry never carries schema or mapping and a declarative
                    -- one always carries at least one, so their absence identifies "registered" here
                    -- without duplicating the registry's key list into SQL.
                    IF @EndpointId IS NULL AND EXISTS (
                        SELECT 1 FROM connections c JOIN connectors i ON i.id=c.connector_id
                        WHERE c.tenant_id=@TenantId AND c.id=@ConnectionId
                          AND JSON_QUERY(i.manifest, '$.source_contracts[0]') IS NOT NULL
                          AND JSON_QUERY(i.manifest, '$.source_contracts[0].schema') IS NULL
                          AND JSON_QUERY(i.manifest, '$.source_contracts[0].mapping') IS NULL)
                    BEGIN
                        SET @EndpointId=NEWID();
                        SELECT @CallbackPath=CONCAT(N'/webhooks/', i.[key], N'/', LOWER(CONVERT(nvarchar(36), @EndpointId)))
                        FROM connections c JOIN connectors i ON i.id=c.connector_id
                        WHERE c.tenant_id=@TenantId AND c.id=@ConnectionId;
                        INSERT INTO source_endpoints (id, tenant_id, topic_id, connection_id, callback_path, status)
                        VALUES (@EndpointId, @TenantId, @TopicId, @ConnectionId, @CallbackPath, N'active');
                        SELECT @EndpointCreatedAt=created_at FROM source_endpoints WHERE id=@EndpointId;
                    END;
                    SELECT a.created_at AS SourceCreatedAt, @EndpointId AS EndpointId,
                           @CallbackPath AS CallbackPath, @EndpointCreatedAt AS EndpointCreatedAt
                    FROM @activated a;
                    """
                : """
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
                    -- No Source entity exists yet to pick a source_contracts[] entry by key, so entry
                    -- 0 transitionally stands in for "the" Source. Only a *registered* (compiled)
                    -- contract has a runtime handler to serve a live webhook; the manifest parser
                    -- guarantees a registered entry never carries schema or mapping and a declarative
                    -- one always carries at least one, so their absence identifies "registered" here
                    -- without duplicating the registry's key list into SQL.
                    adapter_eligible AS (
                        SELECT gen_random_uuid() AS endpoint_id, i.key AS connector_key
                        FROM connections c
                        JOIN connectors i ON i.id = c.connector_id
                        WHERE c.tenant_id = @TenantId AND c.id = @ConnectionId
                          AND jsonb_array_length(i.manifest -> 'source_contracts') > 0
                          AND (i.manifest -> 'source_contracts' -> 0 -> 'schema') IS NULL
                          AND (i.manifest -> 'source_contracts' -> 0 -> 'mapping') IS NULL
                          AND NOT EXISTS (SELECT 1 FROM existing_endpoint)
                    ),
                    inserted_endpoint AS (
                        INSERT INTO source_endpoints (id, tenant_id, topic_id, connection_id, callback_path, status)
                        SELECT endpoint_id, @TenantId, @TopicId, @ConnectionId,
                               '/webhooks/' || connector_key || '/' || endpoint_id::text, 'active'
                        FROM adapter_eligible
                        RETURNING id, callback_path, created_at
                    )
                    SELECT a.created_at AS SourceCreatedAt, COALESCE(ie.id, ee.id) AS EndpointId,
                           COALESCE(ie.callback_path, ee.callback_path) AS CallbackPath,
                           COALESCE(ie.created_at, ee.created_at) AS EndpointCreatedAt
                    FROM activated a LEFT JOIN inserted_endpoint ie ON true LEFT JOIN existing_endpoint ee ON true
                    """;
            var write = await db.QuerySingleAsync<SourceWriteRow>(
                new CommandDefinition(
                    sql,
                    new { TenantId = tenantId, TopicId = topicId, ConnectionId = connectionId },
                    tx,
                    cancellationToken: ct));

            sources.Add(write.ToTopicSource(tenantId, topicId, connectionId));
        }

        return sources.OrderBy(static source => source.ConnectionId).ToList();
    }

    private static async Task RemoveSourcesNotInAsync(
        DbConnection db,
        bool sqlServer,
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
        string now = sqlServer ? "SYSUTCDATETIME()" : "now()";
        string excluded = sqlServer
            ? "connection_id NOT IN @ConnectionIds"
            : "NOT (connection_id = ANY(@ConnectionIds))";

        await db.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE source_endpoints
                SET status = 'revoked', revoked_at = {now}
                WHERE tenant_id = @TenantId
                  AND topic_id = @TopicId
                  AND status = 'active'
                  AND {excluded}
                """,
                parameters,
                tx,
                cancellationToken: ct));

        await db.ExecuteAsync(
            new CommandDefinition(
                $"""
                UPDATE topic_sources
                SET status = 'inactive', inactive_at = {now}
                WHERE tenant_id = @TenantId
                  AND topic_id = @TopicId
                  AND status = 'active'
                  AND {excluded}
                """,
                parameters,
                tx,
                cancellationToken: ct));
    }

    private static async Task<Dictionary<Guid, List<TopicSource>>> LoadSourcesForTopicsAsync(
        DbConnection db,
        bool sqlServer,
        Guid tenantId,
        Guid[] topicIds,
        CancellationToken ct,
        DbTransaction? transaction = null)
    {
        string topicPredicate = sqlServer ? "ts.topic_id IN @TopicIds" : "ts.topic_id = ANY(@TopicIds)";
        var rows = await db.QueryAsync<TopicSourceWithTopicRow>(
            new CommandDefinition(
                $"""
                SELECT
                    ts.tenant_id AS TenantId,
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
                WHERE ts.tenant_id = @TenantId AND {topicPredicate} AND ts.status = 'active'
                """,
                new { TenantId = tenantId, TopicIds = topicIds },
                transaction,
                cancellationToken: ct));

        var map = new Dictionary<Guid, List<TopicSource>>();
        foreach (var row in rows.OrderBy(static row => row.ConnectionId))
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

        public TopicSource ToTopicSource(Guid tenantId, Guid topicId, Guid connectionId) => new()
        {
            TenantId = tenantId,
            TopicId = topicId,
            ConnectionId = connectionId,
            Status = TopicSourceStatus.Active,
            CreatedAt = SourceCreatedAt,
            Endpoint = EndpointId is null
                ? null
                : new SourceEndpoint { Id = EndpointId.Value, CallbackPath = CallbackPath!, CreatedAt = EndpointCreatedAt!.Value },
        };
    }

    private sealed record TopicSourceWithTopicRow
    {
        public Guid TenantId { get; init; }
        public Guid TopicId { get; init; }
        public Guid ConnectionId { get; init; }
        public DateTimeOffset SourceCreatedAt { get; init; }
        public Guid? EndpointId { get; init; }
        public string? CallbackPath { get; init; }
        public DateTimeOffset? EndpointCreatedAt { get; init; }

        public TopicSource ToTopicSource() => new()
        {
            TenantId = TenantId,
            TopicId = TopicId,
            ConnectionId = ConnectionId,
            Status = TopicSourceStatus.Active,
            CreatedAt = SourceCreatedAt,
            Endpoint = EndpointId is null
                ? null
                : new SourceEndpoint { Id = EndpointId.Value, CallbackPath = CallbackPath!, CreatedAt = EndpointCreatedAt!.Value },
        };
    }

}
