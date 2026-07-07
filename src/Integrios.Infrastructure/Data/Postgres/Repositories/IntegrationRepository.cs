using System.Text.Json;
using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;

namespace Integrios.Infrastructure.Data;

public sealed class IntegrationRepository(IDbConnectionFactory connectionFactory) : IIntegrationRepository
{
    private const string SelectColumns =
        "id, key, name, direction, supported_auth_schemes::text AS supported_auth_schemes_json, status, description, created_at, updated_at";

    public async Task<Integration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM integrations
            WHERE id = @Id
            LIMIT 1
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IntegrationRow? row = await db.QuerySingleOrDefaultAsync<IntegrationRow>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));

        return row?.ToIntegration();
    }

    public async Task<(IReadOnlyList<Integration> Items, string? NextCursor)> ListAsync(
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorTime, out cursorId);

        var sql = hasCursor
            ? $"""
                SELECT {SelectColumns}
                FROM integrations
                WHERE (created_at, id) > (@CursorTime, @CursorId)
                ORDER BY created_at, id
                LIMIT @Limit
                """
            : $"""
                SELECT {SelectColumns}
                FROM integrations
                ORDER BY created_at, id
                LIMIT @Limit
                """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await db.QueryAsync<IntegrationRow>(
            new CommandDefinition(
                sql,
                new { CursorTime = cursorTime, CursorId = cursorId, Limit = limit + 1 },
                cancellationToken: cancellationToken))).ToList();

        if (rows.Count == 0)
        {
            return ([], null);
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = PageCursor.Encode(rows[^1].CreatedAt, rows[^1].Id);
        }

        return (rows.Select(r => r.ToIntegration()).ToList(), nextCursor);
    }

    private sealed record IntegrationRow
    {
        public Guid Id { get; init; }
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Direction { get; init; } = "";
        public string SupportedAuthSchemesJson { get; init; } = "[]";
        public string Status { get; init; } = "";
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Integration ToIntegration() => new()
        {
            Id = Id,
            Key = Key,
            Name = Name,
            Direction = Enum.Parse<IntegrationDirection>(Direction, ignoreCase: true),
            SupportedAuthSchemes = JsonSerializer.Deserialize<string[]>(SupportedAuthSchemesJson) ?? [],
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
