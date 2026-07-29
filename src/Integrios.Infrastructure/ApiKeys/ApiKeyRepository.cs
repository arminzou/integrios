using Dapper;
using Integrios.Application.ApiKeys;
using Integrios.Infrastructure.Data;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Tenants;

namespace Integrios.Infrastructure.ApiKeys;

internal sealed class ApiKeyRepository(IDbConnectionFactory connectionFactory) : IApiKeyRepository
{
    public async Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO api_keys (id, tenant_id, name, key_prefix, key_hash, status, description, created_at, expires_at)
            VALUES (@Id, @TenantId, @Name, @KeyPrefix, @KeyHash, @Status, @Description, @CreatedAt, @ExpiresAt)
            RETURNING id, tenant_id, name, key_prefix, key_hash, status, description, created_at, expires_at, last_used_at, revoked_at
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        ApiKeyRow row = await connection.QuerySingleAsync<ApiKeyRow>(sql, new
        {
            apiKey.Id,
            apiKey.TenantId,
            apiKey.Name,
            KeyPrefix = apiKey.KeyPrefix,
            KeyHash = apiKey.KeyHash,
            Status = apiKey.Status.ToString().ToLowerInvariant(),
            apiKey.Description,
            apiKey.CreatedAt,
            apiKey.ExpiresAt,
        });
        return row.ToApiKey();
    }

    // Admin plane: get one key scoped to a tenant
    public async Task<ApiKey?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, tenant_id, name, key_prefix, key_hash, status, description, created_at, expires_at, last_used_at, revoked_at
            FROM api_keys
            WHERE tenant_id = @TenantId AND id = @Id
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ApiKeyRow>(sql, new { TenantId = tenantId, Id = id });
        return row?.ToApiKey();
    }

    // Admin plane: list keys for a tenant with cursor-based pagination
    public async Task<(IReadOnlyList<ApiKey> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId, string? afterCursor, int limit, CancellationToken cancellationToken = default)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorCreatedAt, out cursorId);

        const string sql = """
            SELECT id, tenant_id, name, key_prefix, key_hash, status, description, created_at, expires_at, last_used_at, revoked_at
            FROM api_keys
            WHERE tenant_id = @TenantId
              AND (NOT @HasCursor
                   OR created_at > @CursorCreatedAt
                   OR (created_at = @CursorCreatedAt AND id > @CursorId))
            ORDER BY created_at ASC, id ASC
            LIMIT @Limit
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ApiKeyRow>(sql, new
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

        return (rows.Select(r => r.ToApiKey()).ToList(), nextCursor);
    }

    // Admin plane: revoke a key (tenant-scoped — cross-tenant revoke is impossible)
    public async Task<bool> RevokeAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE api_keys
            SET status = 'disabled', revoked_at = now()
            WHERE tenant_id = @TenantId AND id = @Id AND status = 'active'
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        int affected = await connection.ExecuteAsync(sql, new { TenantId = tenantId, Id = id });
        return affected > 0;
    }

    // Row type for admin queries (direct column names)
    private sealed record ApiKeyRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public string Name { get; init; } = "";
        public string KeyPrefix { get; init; } = "";
        public string KeyHash { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }

        public ApiKey ToApiKey() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            KeyPrefix = KeyPrefix,
            KeyHash = KeyHash,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt,
            LastUsedAt = LastUsedAt,
            RevokedAt = RevokedAt,
            Description = Description,
        };
    }

}
