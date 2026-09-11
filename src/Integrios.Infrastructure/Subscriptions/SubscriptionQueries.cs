using System.Text.Json;
using Dapper;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Common.Exceptions;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Subscriptions;

internal sealed class SubscriptionQueries(
    IDbConnectionFactory connectionFactory,
    IDataProtectionProvider dataProtectionProvider) : ISubscriptionQueries
{
    public async Task<SubscriptionListDto> ListByTopicAsync(
        Guid tenantId,
        Guid topicId,
        OperationalStatus? status,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        // Reproduced character for character from the EF implementation this read replaced. The scope
        // is baked into every cursor already issued, so reformatting it - including tidying it to
        // match the serialized scope the Tenant-wide read below builds - rejects every cursor an
        // Operator is holding at the moment the change deploys.
        string scope = $"subscriptions:{tenantId:N}:{topicId:N}:{status?.ToString() ?? "all"}";
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        if (afterCursor is not null
            && !PageCursor.TryDecode(dataProtectionProvider, afterCursor, scope, out cursorTime, out cursorId))
            throw new InvalidCursorException();

        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        var where = new List<string> { "s.tenant_id = @TenantId", "s.topic_id = @TopicId" };
        if (status is not null) where.Add("s.status = @Status");
        if (afterCursor is not null)
            where.Add("(s.created_at < @CursorTime OR (s.created_at = @CursorTime AND s.id < @CursorId))");
        string sql = $"""
            SELECT {(sqlServer ? "TOP (@Take)" : "")}
                s.id AS Id, s.topic_id AS TopicId, s.tenant_id AS TenantId, s.name AS Name,
                s.destination_id AS DestinationId,
                d.name AS DestinationName, s.status AS Status, s.order_index AS OrderIndex,
                s.description AS Description, s.created_at AS CreatedAt, s.updated_at AS UpdatedAt
            FROM subscriptions s
            INNER JOIN destinations d
                ON d.tenant_id = s.tenant_id AND d.id = s.destination_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY s.created_at DESC, s.id DESC
            {(sqlServer ? "" : "LIMIT @Take")}
            """;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SubscriptionRow>(new CommandDefinition(sql, new
        {
            TenantId = tenantId,
            TopicId = topicId,
            Status = status is { } statusFilter ? StoredEnum(statusFilter) : null,
            CursorTime = cursorTime,
            CursorId = cursorId,
            Take = limit + 1,
        }, cancellationToken: cancellationToken))).AsList();
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new SubscriptionListDto(rows.Select(row => new SubscriptionListItemDto(
            row.Id,
            row.TopicId,
            row.TenantId,
            row.Name,
            row.DestinationId,
            row.DestinationName,
            row.Status,
            row.OrderIndex,
            row.Description,
            row.CreatedAt,
            row.UpdatedAt)).ToList(),
            hasMore ? PageCursor.Encode(dataProtectionProvider, scope, rows[^1].CreatedAt, rows[^1].Id, DateTimeOffset.UtcNow) : null);
    }

    public async Task<SubscriptionByTenantListDto> ListByTenantAsync(
        Guid tenantId,
        SubscriptionListFilter filter,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        string scope = "tenant-subscriptions:" + JsonSerializer.Serialize(new
        {
            tenantId,
            filter.Status,
            filter.TopicId,
            filter.DestinationId,
            filter.NameContains,
        });
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        if (afterCursor is not null
            && !PageCursor.TryDecode(dataProtectionProvider, afterCursor, scope, out cursorTime, out cursorId))
            throw new InvalidCursorException();

        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        var where = new List<string> { "s.tenant_id = @TenantId" };
        if (filter.Status is not null) where.Add("s.status = @Status");
        if (filter.TopicId is not null) where.Add("s.topic_id = @TopicId");
        if (filter.DestinationId is not null)
            where.Add("s.destination_id = @DestinationId");
        if (filter.NameContains is not null)
            where.Add(sqlServer
                ? "CHARINDEX(@NameContains, LOWER(s.name)) > 0"
                : "POSITION(@NameContains IN LOWER(s.name)) > 0");
        if (afterCursor is not null)
            where.Add("(s.created_at < @CursorTime OR (s.created_at = @CursorTime AND s.id < @CursorId))");
        string sql = $"""
            SELECT {(sqlServer ? "TOP (@Take)" : "")}
                s.id AS Id, s.topic_id AS TopicId, t.name AS TopicName, s.tenant_id AS TenantId,
                s.name AS Name, s.destination_id AS DestinationId,
                d.name AS DestinationName, s.status AS Status, s.order_index AS OrderIndex,
                s.description AS Description, s.created_at AS CreatedAt, s.updated_at AS UpdatedAt
            FROM subscriptions s
            INNER JOIN topics t ON t.tenant_id = s.tenant_id AND t.id = s.topic_id
            INNER JOIN destinations d
                ON d.tenant_id = s.tenant_id AND d.id = s.destination_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY s.created_at DESC, s.id DESC
            {(sqlServer ? "" : "LIMIT @Take")}
            """;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SubscriptionRow>(new CommandDefinition(sql, new
        {
            TenantId = tenantId,
            Status = filter.Status is { } statusFilter ? StoredEnum(statusFilter) : null,
            TopicId = filter.TopicId,
            DestinationId = filter.DestinationId,
            NameContains = filter.NameContains?.ToLowerInvariant(),
            CursorTime = cursorTime,
            CursorId = cursorId,
            Take = limit + 1,
        }, cancellationToken: cancellationToken))).AsList();
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new SubscriptionByTenantListDto(rows.Select(row => new SubscriptionByTenantListItemDto(
            row.Id,
            row.TopicId,
            row.TopicName,
            row.TenantId,
            row.Name,
            row.DestinationId,
            row.DestinationName,
            row.Status,
            row.OrderIndex,
            row.Description,
            row.CreatedAt,
            row.UpdatedAt)).ToList(),
            hasMore ? PageCursor.Encode(dataProtectionProvider, scope, rows[^1].CreatedAt, rows[^1].Id, DateTimeOffset.UtcNow) : null);
    }

    private static string StoredEnum<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private sealed record SubscriptionRow
    {
        public Guid Id { get; init; }
        public Guid TopicId { get; init; }
        public string TopicName { get; init; } = "";
        public Guid TenantId { get; init; }
        public string Name { get; init; } = "";
        public Guid DestinationId { get; init; }
        public string DestinationName { get; init; } = "";
        public string Status { get; init; } = "";
        public int OrderIndex { get; init; }
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }
}
