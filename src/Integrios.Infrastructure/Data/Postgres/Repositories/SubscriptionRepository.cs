using System.Text.Json;
using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Topics;

namespace Integrios.Infrastructure.Data;

public sealed class SubscriptionRepository(IDbConnectionFactory connectionFactory) : ISubscriptionRepository
{
    private const string AdminSelectColumns =
        """
        s.id AS Id,
        s.topic_id AS TopicId,
        t.tenant_id AS TenantId,
        s.name AS Name,
        s.match_rules::text AS MatchRulesJson,
        s.destination_connection_id AS DestinationConnectionId,
        s.transform_config::text AS TransformConfigJson,
        s.delivery_policy::text AS DeliveryPolicyJson,
        s.dlq_enabled AS DlqEnabled,
        s.status AS Status,
        s.order_index AS OrderIndex,
        s.description AS Description,
        s.created_at AS CreatedAt,
        s.updated_at AS UpdatedAt
        """;

    public async Task<Subscription> CreateAsync(
        Guid tenantId,
        Guid topicId,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        bool dlqEnabled,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleAsync<SubscriptionAdminRow>(
            new CommandDefinition(
                $"""
                WITH inserted AS (
                    INSERT INTO subscriptions (
                        id,
                        topic_id,
                        name,
                        match_rules,
                        destination_connection_id,
                        dlq_enabled,
                        status,
                        order_index,
                        description,
                        created_at,
                        updated_at)
                    SELECT
                        @Id,
                        t.id,
                        @Name,
                        CAST(@MatchRulesJson AS jsonb),
                        c.id,
                        @DlqEnabled,
                        'active',
                        @OrderIndex,
                        @Description,
                        @Now,
                        @Now
                    FROM topics t
                    JOIN connections c ON c.id = @DestinationConnectionId AND c.tenant_id = t.tenant_id
                    WHERE t.id = @TopicId AND t.tenant_id = @TenantId
                    RETURNING *
                )
                SELECT {AdminSelectColumns}
                FROM inserted s
                JOIN topics t ON t.id = s.topic_id
                """,
                new
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    TopicId = topicId,
                    Name = name,
                    MatchRulesJson = matchRules.GetRawText(),
                    DestinationConnectionId = destinationConnectionId,
                    DlqEnabled = dlqEnabled,
                    OrderIndex = orderIndex,
                    Description = description,
                    Now = DateTimeOffset.UtcNow,
                },
                cancellationToken: cancellationToken));

        return row.ToSubscription();
    }

    public async Task<Subscription?> GetByIdAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SubscriptionAdminRow>(
            new CommandDefinition(
                $"""
                SELECT {AdminSelectColumns}
                FROM subscriptions s
                JOIN topics t ON t.id = s.topic_id
                WHERE t.tenant_id = @TenantId
                  AND s.topic_id = @TopicId
                  AND s.id = @Id
                LIMIT 1
                """,
                new { TenantId = tenantId, TopicId = topicId, Id = id },
                cancellationToken: cancellationToken));

        return row?.ToSubscription();
    }

    public async Task<(IReadOnlyList<Subscription> Items, string? NextCursor)> ListByTopicAsync(
        Guid tenantId,
        Guid topicId,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        var hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorTime, out cursorId);

        var sql = hasCursor
            ? $"""
               SELECT {AdminSelectColumns}
               FROM subscriptions s
               JOIN topics t ON t.id = s.topic_id
               WHERE t.tenant_id = @TenantId
                 AND s.topic_id = @TopicId
                 AND (s.created_at, s.id) > (@CursorTime, @CursorId)
               ORDER BY s.created_at, s.id
               LIMIT @Limit
               """
            : $"""
               SELECT {AdminSelectColumns}
               FROM subscriptions s
               JOIN topics t ON t.id = s.topic_id
               WHERE t.tenant_id = @TenantId
                 AND s.topic_id = @TopicId
               ORDER BY s.created_at, s.id
               LIMIT @Limit
               """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SubscriptionAdminRow>(
            new CommandDefinition(
                sql,
                new { TenantId = tenantId, TopicId = topicId, CursorTime = cursorTime, CursorId = cursorId, Limit = limit },
                cancellationToken: cancellationToken))).ToList();

        if (rows.Count == 0)
            return ([], null);

        var items = rows.Select(static row => row.ToSubscription()).ToList();
        var nextCursor = rows.Count == limit
            ? PageCursor.Encode(rows[^1].CreatedAt, rows[^1].Id)
            : null;

        return (items, nextCursor);
    }

    public async Task<Subscription?> UpdateAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        bool dlqEnabled,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SubscriptionAdminRow>(
            new CommandDefinition(
                $"""
                WITH updated AS (
                    UPDATE subscriptions s
                    SET name = @Name,
                        match_rules = CAST(@MatchRulesJson AS jsonb),
                        destination_connection_id = c.id,
                        dlq_enabled = @DlqEnabled,
                        order_index = @OrderIndex,
                        description = @Description,
                        updated_at = now()
                    FROM topics t
                    JOIN connections c ON c.id = @DestinationConnectionId AND c.tenant_id = t.tenant_id
                    WHERE s.id = @Id
                      AND s.topic_id = @TopicId
                      AND s.topic_id = t.id
                      AND t.tenant_id = @TenantId
                      AND s.status != 'disabled'
                    RETURNING s.*
                )
                SELECT {AdminSelectColumns}
                FROM updated s
                JOIN topics t ON t.id = s.topic_id
                """,
                new
                {
                    TenantId = tenantId,
                    TopicId = topicId,
                    Id = id,
                    Name = name,
                    MatchRulesJson = matchRules.GetRawText(),
                    DestinationConnectionId = destinationConnectionId,
                    DlqEnabled = dlqEnabled,
                    OrderIndex = orderIndex,
                    Description = description,
                },
                cancellationToken: cancellationToken));

        return row?.ToSubscription();
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE subscriptions s
                SET status = 'disabled', updated_at = now()
                FROM topics t
                WHERE s.id = @Id
                  AND s.topic_id = @TopicId
                  AND s.topic_id = t.id
                  AND t.tenant_id = @TenantId
                  AND s.status != 'disabled'
                """,
                new { TenantId = tenantId, TopicId = topicId, Id = id },
                cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<IReadOnlyList<SubscriptionTarget>> GetActiveSubscriptionsAsync(
        Guid topicId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<SubscriptionWorkerRow>(
            new CommandDefinition(
                """
                SELECT
                    s.id                        AS Id,
                    s.name                      AS Name,
                    s.match_rules::text         AS MatchRulesJson,
                    s.destination_connection_id AS DestinationConnectionId,
                    c.config->>'url'            AS DestinationUrl
                FROM subscriptions s
                JOIN connections c ON c.id = s.destination_connection_id
                WHERE s.topic_id = @TopicId
                  AND s.status   = 'active'
                ORDER BY s.order_index
                """,
                new { TopicId = topicId },
                cancellationToken: cancellationToken));

        return rows.Select(s => new SubscriptionTarget(
            s.Id,
            s.Name,
            ParseMatchEventTypes(s.MatchRulesJson),
            s.DestinationConnectionId,
            s.DestinationUrl ?? "")).ToList();
    }

    private static string[] ParseMatchEventTypes(string? matchRulesJson)
    {
        if (string.IsNullOrWhiteSpace(matchRulesJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(matchRulesJson);

            if (doc.RootElement.TryGetProperty("event_type", out var single) && single.ValueKind == JsonValueKind.String)
            {
                var value = single.GetString();
                return string.IsNullOrWhiteSpace(value) ? [] : [value];
            }

            if (doc.RootElement.TryGetProperty("event_types", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(static s => s.Length > 0)
                    .ToArray();
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private sealed record SubscriptionWorkerRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? MatchRulesJson { get; init; }
        public Guid DestinationConnectionId { get; init; }
        public string? DestinationUrl { get; init; }
    }

    private sealed record SubscriptionAdminRow
    {
        public Guid Id { get; init; }
        public Guid TopicId { get; init; }
        public Guid TenantId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string MatchRulesJson { get; init; } = "{}";
        public Guid DestinationConnectionId { get; init; }
        public string? TransformConfigJson { get; init; }
        public string? DeliveryPolicyJson { get; init; }
        public bool DlqEnabled { get; init; }
        public string Status { get; init; } = string.Empty;
        public int OrderIndex { get; init; }
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Subscription ToSubscription() => new()
        {
            Id = Id,
            TopicId = TopicId,
            TenantId = TenantId,
            Name = Name,
            MatchRules = JsonSerializer.Deserialize<JsonElement>(MatchRulesJson),
            DestinationConnectionId = DestinationConnectionId,
            TransformConfig = string.IsNullOrWhiteSpace(TransformConfigJson)
                ? null
                : JsonSerializer.Deserialize<JsonElement>(TransformConfigJson),
            DeliveryPolicy = string.IsNullOrWhiteSpace(DeliveryPolicyJson)
                ? null
                : JsonSerializer.Deserialize<JsonElement>(DeliveryPolicyJson),
            DlqEnabled = DlqEnabled,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            OrderIndex = OrderIndex,
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
