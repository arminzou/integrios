using System.Text.Json;
using Dapper;
using Integrios.Application.Authoring.Sources;
using Integrios.Application.Common.Exceptions;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Sources;

internal sealed class SourceQueries(IDbConnectionFactory connectionFactory, IDataProtectionProvider dataProtectionProvider) : ISourceQueries
{
    public async Task<SourceListDto> ListAsync(Guid tenantId, SourceStatus? status, SourceType? type, Guid? topicId, string? afterCursor, int limit, CancellationToken cancellationToken)
    {
        string scope = "sources:" + JsonSerializer.Serialize(new { tenantId, status, type, topicId });
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        if (afterCursor is not null && !PageCursor.TryDecode(dataProtectionProvider, afterCursor, scope, out cursorTime, out cursorId))
            throw new InvalidCursorException();

        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        var where = new List<string> { "tenant_id = @TenantId" };
        if (status is not null) where.Add("status = @Status");
        if (type is not null) where.Add("type = @Type");
        if (topicId is not null) where.Add("topic_id = @TopicId");
        if (afterCursor is not null)
            where.Add("(created_at < @CursorTime OR (created_at = @CursorTime AND id < @CursorId))");
        string sql = $"""
            SELECT {(sqlServer ? "TOP (@Take)" : "")}
                id AS Id, tenant_id AS TenantId, connector_id AS ConnectorId, topic_id AS TopicId,
                type AS Type, status AS Status,
                {(sqlServer
                    ? "COALESCE(input_requirements, '')"
                    : "COALESCE(input_requirements::text, '')")} AS InputRequirements,
                created_at AS CreatedAt, updated_at AS UpdatedAt, revoked_at AS RevokedAt
            FROM sources
            WHERE {string.Join(" AND ", where)}
            ORDER BY created_at DESC, id DESC
            {(sqlServer ? "" : "LIMIT @Take")}
            """;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SourceRow>(new CommandDefinition(sql, new
        {
            TenantId = tenantId,
            Status = status is { } statusFilter ? StoredEnum(statusFilter) : null,
            Type = type is { } typeFilter ? StoredEnum(typeFilter) : null,
            TopicId = topicId,
            CursorTime = cursorTime,
            CursorId = cursorId,
            Take = limit + 1,
        }, cancellationToken: cancellationToken))).AsList();
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new SourceListDto(rows.Select(row => new SourceListItemDto(
            row.Id, row.TenantId, row.ConnectorId, row.TopicId, row.Type, row.Status, row.InputRequirements,
            row.CreatedAt, row.UpdatedAt, row.RevokedAt)).ToList(),
            hasMore ? PageCursor.Encode(dataProtectionProvider, scope, rows[^1].CreatedAt, rows[^1].Id, DateTimeOffset.UtcNow) : null);
    }

    /// The value EF stores these enums under, spelled the one way SnakeCaseEnumConverter spells it.
    /// This read bypasses EF, and a hand-lowered name would silently stop matching the column the
    /// first time an enum gains a member of more than one word.
    private static string StoredEnum<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private sealed record SourceRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public Guid ConnectorId { get; init; }
        public Guid TopicId { get; init; }
        public string Type { get; init; } = "";
        public string Status { get; init; } = "";
        public string InputRequirements { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }
    }
}
