using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using Npgsql;

namespace Integrios.Infrastructure.Data;

public sealed class TenantRepository(IDbConnectionFactory connectionFactory) : ITenantRepository
{
    // Postgres error code for unique_violation
    private const string UniqueViolation = "23505";

    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO tenants (id, slug, name, status, environment, description, created_at, updated_at)
            VALUES (@Id, @Slug, @Name, @Status, @Environment, @Description, @CreatedAt, @UpdatedAt)
            RETURNING id, slug, name, status, environment, description, created_at, updated_at
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        try
        {
            var row = await connection.QuerySingleAsync<TenantRow>(sql, new
            {
                tenant.Id,
                tenant.Slug,
                tenant.Name,
                Status = tenant.Status.ToString().ToLowerInvariant(),
                tenant.Environment,
                tenant.Description,
                tenant.CreatedAt,
                tenant.UpdatedAt,
            });
            return row.ToTenant();
        }
        catch (NpgsqlException ex) when (ex.SqlState == UniqueViolation)
        {
            throw new InvalidOperationException($"A tenant with slug '{tenant.Slug}' already exists.", ex);
        }
    }

    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, slug, name, status, environment, description, created_at, updated_at
            FROM tenants
            WHERE id = @Id
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TenantRow>(sql, new { Id = id });
        return row?.ToTenant();
    }

    public async Task<(IReadOnlyList<Tenant> Items, string? NextCursor)> ListAsync(
        string? afterCursor, int limit, CancellationToken cancellationToken = default)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        const string sql = """
            SELECT id, slug, name, status, environment, description, created_at, updated_at
            FROM tenants
            WHERE (NOT @HasCursor
                OR created_at > @CursorCreatedAt
                OR (created_at = @CursorCreatedAt AND id > @CursorId))
            ORDER BY created_at ASC, id ASC
            LIMIT @Limit
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TenantRow>(sql, new
        {
            HasCursor = hasCursor,
            CursorCreatedAt = cursorCreatedAt,
            CursorId = cursorId,
            Limit = limit + 1,
        });

        var list = rows.ToList();
        string? nextCursor = null;

        if (list.Count > limit)
        {
            list.RemoveAt(list.Count - 1);
            var last = list[^1];
            nextCursor = PageCursor.Encode(last.CreatedAt, last.Id);
        }

        return (list.Select(r => r.ToTenant()).ToList(), nextCursor);
    }

    public async Task<Tenant?> UpdateAsync(
        Guid id, string name, string? description, string? environment,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE tenants
            SET name = @Name, description = @Description, environment = @Environment, updated_at = now()
            WHERE id = @Id
            RETURNING id, slug, name, status, environment, description, created_at, updated_at
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TenantRow>(sql, new
        {
            Id = id,
            Name = name,
            Description = description,
            Environment = environment,
        });
        return row?.ToTenant();
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE tenants
            SET status = 'disabled', updated_at = now()
            WHERE id = @Id AND status = 'active'
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(sql, new { Id = id });
        return affected > 0;
    }

    private sealed record TenantRow
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = "";
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Environment { get; init; }
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Tenant ToTenant() => new()
        {
            Id = Id,
            Slug = Slug,
            Name = Name,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Environment = Environment,
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
