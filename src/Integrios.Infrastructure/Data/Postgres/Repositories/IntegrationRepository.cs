using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;

namespace Integrios.Infrastructure.Data;

public sealed class IntegrationRepository(IDbConnectionFactory connectionFactory) : IIntegrationRepository
{
    private const string SelectColumns = "id, key, name, direction, auth_scheme, status, description, created_at, updated_at";

    public async Task<Integration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM integrations
            WHERE id = @Id
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await db.QuerySingleOrDefaultAsync<IntegrationRow>(sql, new { Id = id });
        return row?.ToIntegration();
    }

    public async Task<(IReadOnlyList<Integration> Items, string? NextCursor)> ListAsync(
        string? afterCursor, int limit, CancellationToken cancellationToken = default)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        const string sql = $"""
            SELECT {SelectColumns}
            FROM integrations
            WHERE (NOT @HasCursor
                   OR created_at > @CursorCreatedAt
                   OR (created_at = @CursorCreatedAt AND id > @CursorId))
            ORDER BY created_at ASC, id ASC
            LIMIT @Limit
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await db.QueryAsync<IntegrationRow>(sql, new
        {
            HasCursor = hasCursor,
            CursorCreatedAt = cursorCreatedAt,
            CursorId = cursorId,
            Limit = limit + 1,
        })).ToList();

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = PageCursor.Encode(last.CreatedAt, last.Id);
        }

        return (rows.Select(r => r.ToIntegration()).ToList(), nextCursor);
    }

    private sealed record IntegrationRow
    {
        public Guid Id { get; init; }
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Direction { get; init; } = "";
        public string AuthScheme { get; init; } = "";
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
            AuthScheme = Enum.Parse<IntegrationAuthScheme>(AuthScheme, ignoreCase: true),
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
