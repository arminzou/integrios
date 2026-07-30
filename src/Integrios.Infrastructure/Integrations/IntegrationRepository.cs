using System.Text.Json;
using Dapper;
using Integrios.Application.Integrations;
using Integrios.Infrastructure.Data;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;

namespace Integrios.Infrastructure.Integrations;

internal sealed class IntegrationRepository(IDbConnectionFactory connectionFactory)
    : IIntegrationCatalog, IBuiltinIntegrationReconciler
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

    // Bootstrap: reconcile a platform-owned built-in row by key, overwriting any drift on re-run
    public async Task<Integration> ReconcileAsync(Integration integration, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            INSERT INTO integrations (id, key, name, direction, supported_auth_schemes, status, description, created_at, updated_at)
            VALUES (@Id, @Key, @Name, @Direction, @SupportedAuthSchemes::jsonb, @Status, @Description, @CreatedAt, @UpdatedAt)
            ON CONFLICT (key) DO UPDATE SET
                name = EXCLUDED.name,
                direction = EXCLUDED.direction,
                supported_auth_schemes = EXCLUDED.supported_auth_schemes,
                status = EXCLUDED.status,
                description = EXCLUDED.description,
                updated_at = EXCLUDED.updated_at
            RETURNING {SelectColumns}
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IntegrationRow row = await db.QuerySingleAsync<IntegrationRow>(new CommandDefinition(
            sql,
            new
            {
                integration.Id,
                integration.Key,
                integration.Name,
                Direction = integration.Direction.ToString().ToLowerInvariant(),
                SupportedAuthSchemes = JsonSerializer.Serialize(integration.SupportedAuthSchemes),
                Status = integration.Status.ToString().ToLowerInvariant(),
                integration.Description,
                integration.CreatedAt,
                integration.UpdatedAt,
            },
            cancellationToken: cancellationToken));

        return row.ToIntegration();
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
