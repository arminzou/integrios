using System.Text.Json;
using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Npgsql;

namespace Integrios.Infrastructure.Data;

public sealed class ConnectionRepository(IDbConnectionFactory connectionFactory) : IConnectionRepository
{
    private const string ForeignKeyViolation = "23503";

    private const string SelectColumns = """
        id, tenant_id, integration_id, name,
        config::text AS ConfigJson, secret_refs::text AS SecretRefsJson,
        status, environment, description, created_at, updated_at
        """;

    public async Task<Connection> CreateAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            INSERT INTO connections (id, tenant_id, integration_id, name, config, secret_refs, status, environment, description, created_at, updated_at)
            VALUES (@Id, @TenantId, @IntegrationId, @Name, @Config::jsonb, @SecretRefs::jsonb, @Status, @Environment, @Description, @CreatedAt, @UpdatedAt)
            RETURNING {SelectColumns}
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        try
        {
            var row = await db.QuerySingleAsync<ConnectionRow>(sql, new
            {
                connection.Id,
                connection.TenantId,
                connection.IntegrationId,
                connection.Name,
                Config = JsonSerializer.Serialize(connection.Config),
                SecretRefs = JsonSerializer.Serialize(connection.SecretReferences),
                Status = connection.Status.ToString().ToLowerInvariant(),
                connection.Environment,
                connection.Description,
                connection.CreatedAt,
                connection.UpdatedAt,
            });
            return row.ToConnection();
        }
        catch (NpgsqlException ex) when (ex.SqlState == ForeignKeyViolation)
        {
            throw new InvalidOperationException("The specified integration does not exist.", ex);
        }
    }

    public async Task<Connection?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM connections
            WHERE tenant_id = @TenantId AND id = @Id
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await db.QuerySingleOrDefaultAsync<ConnectionRow>(sql, new { TenantId = tenantId, Id = id });
        return row?.ToConnection();
    }

    public async Task<(IReadOnlyList<Connection> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        const string sql = $"""
            SELECT {SelectColumns}
            FROM connections
            WHERE tenant_id = @TenantId
              AND (NOT @HasCursor
                   OR created_at > @CursorCreatedAt
                   OR (created_at = @CursorCreatedAt AND id > @CursorId))
            ORDER BY created_at ASC, id ASC
            LIMIT @Limit
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await db.QueryAsync<ConnectionRow>(sql, new
        {
            TenantId = tenantId,
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

        return (rows.Select(r => r.ToConnection()).ToList(), nextCursor);
    }

    public async Task<Connection?> UpdateAsync(
        Guid tenantId, Guid id, string name, JsonElement config, string? environment, string? description,
        CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            UPDATE connections
            SET name = @Name, config = @Config::jsonb, environment = @Environment, description = @Description, updated_at = now()
            WHERE tenant_id = @TenantId AND id = @Id
            RETURNING {SelectColumns}
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await db.QuerySingleOrDefaultAsync<ConnectionRow>(sql, new
        {
            TenantId = tenantId,
            Id = id,
            Name = name,
            Config = JsonSerializer.Serialize(config),
            Environment = environment,
            Description = description,
        });
        return row?.ToConnection();
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE connections
            SET status = 'disabled', updated_at = now()
            WHERE tenant_id = @TenantId AND id = @Id AND status = 'active'
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        int affected = await db.ExecuteAsync(sql, new { TenantId = tenantId, Id = id });
        return affected > 0;
    }

    private sealed record ConnectionRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public Guid IntegrationId { get; init; }
        public string Name { get; init; } = "";
        public string ConfigJson { get; init; } = "{}";
        public string SecretRefsJson { get; init; } = "{}";
        public string Status { get; init; } = "";
        public string? Environment { get; init; }
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Connection ToConnection() => new()
        {
            Id = Id,
            TenantId = TenantId,
            IntegrationId = IntegrationId,
            Name = Name,
            Config = JsonSerializer.Deserialize<JsonElement>(ConfigJson),
            SecretReferences = JsonSerializer.Deserialize<JsonElement>(SecretRefsJson),
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Environment = Environment,
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
