using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Application.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Tenants;

namespace Integrios.Infrastructure.Data;

public sealed class ApiKeyRepository(IDbConnectionFactory connectionFactory) : IApiKeyRepository
{
    // Data plane: resolve tenant context from an incoming ApiKey credential
    public async Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByPublicKeyAsync(
        string publicKey, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                c.id           AS ApiKeyId,
                c.tenant_id    AS ApiKeyTenantId,
                c.name         AS ApiKeyName,
                c.key_id       AS ApiKeyPublicKey,
                c.secret_hash  AS ApiKeySecretHash,
                c.scopes       AS ApiKeyScopes,
                c.status       AS ApiKeyStatus,
                c.created_at   AS ApiKeyCreatedAt,
                c.expires_at   AS ApiKeyExpiresAt,
                c.last_used_at AS ApiKeyLastUsedAt,
                c.description  AS ApiKeyDescription,
                t.id           AS TenantId,
                t.slug         AS TenantSlug,
                t.name         AS TenantName,
                t.status       AS TenantStatus,
                t.environment  AS TenantEnvironment,
                t.created_at   AS TenantCreatedAt,
                t.updated_at   AS TenantUpdatedAt,
                t.description  AS TenantDescription
            FROM api_keys c
            JOIN tenants t ON t.id = c.tenant_id
            WHERE c.key_id = @PublicKey
              AND c.status = 'active'
              AND t.status = 'active'
              AND (c.expires_at IS NULL OR c.expires_at > now())
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<JoinRow>(sql, new { PublicKey = publicKey });
        if (row is null)
            return null;

        return (row.ToApiKey(), row.ToTenant());
    }

    // Admin plane: create a new API key for a tenant
    public async Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO api_keys (id, tenant_id, name, key_id, secret_hash, scopes, status, description, created_at, expires_at)
            VALUES (@Id, @TenantId, @Name, @PublicKey, @SecretHash, @Scopes, @Status, @Description, @CreatedAt, @ExpiresAt)
            RETURNING id, tenant_id, name, key_id, secret_hash, scopes, status, description, created_at, expires_at, last_used_at
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        ApiKeyRow row = await connection.QuerySingleAsync<ApiKeyRow>(sql, new
        {
            apiKey.Id,
            apiKey.TenantId,
            apiKey.Name,
            PublicKey = apiKey.PublicKey,
            apiKey.SecretHash,
            Scopes = apiKey.Scopes.ToArray(),
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
            SELECT id, tenant_id, name, key_id, secret_hash, scopes, status, description, created_at, expires_at, last_used_at
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
            SELECT id, tenant_id, name, key_id, secret_hash, scopes, status, description, created_at, expires_at, last_used_at
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
            SET status = 'disabled'
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
        public string KeyId { get; init; } = "";
        public string SecretHash { get; init; } = "";
        public string[] Scopes { get; init; } = [];
        public string Status { get; init; } = "";
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }

        public ApiKey ToApiKey() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            PublicKey = KeyId,
            SecretHash = SecretHash,
            Scopes = Scopes,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt,
            LastUsedAt = LastUsedAt,
            Description = Description,
        };
    }

    // Row type for the data-plane join query (aliased columns)
    private sealed record JoinRow
    {
        public Guid ApiKeyId { get; init; }
        public Guid ApiKeyTenantId { get; init; }
        public string ApiKeyName { get; init; } = "";
        public string ApiKeyPublicKey { get; init; } = "";
        public string ApiKeySecretHash { get; init; } = "";
        public string[] ApiKeyScopes { get; init; } = [];
        public string ApiKeyStatus { get; init; } = "";
        public DateTimeOffset ApiKeyCreatedAt { get; init; }
        public DateTimeOffset? ApiKeyExpiresAt { get; init; }
        public DateTimeOffset? ApiKeyLastUsedAt { get; init; }
        public string? ApiKeyDescription { get; init; }
        public Guid TenantId { get; init; }
        public string TenantSlug { get; init; } = "";
        public string TenantName { get; init; } = "";
        public string TenantStatus { get; init; } = "";
        public string? TenantEnvironment { get; init; }
        public DateTimeOffset TenantCreatedAt { get; init; }
        public DateTimeOffset TenantUpdatedAt { get; init; }
        public string? TenantDescription { get; init; }

        public ApiKey ToApiKey() => new()
        {
            Id = ApiKeyId,
            TenantId = ApiKeyTenantId,
            Name = ApiKeyName,
            PublicKey = ApiKeyPublicKey,
            SecretHash = ApiKeySecretHash,
            Scopes = ApiKeyScopes,
            Status = Enum.Parse<OperationalStatus>(ApiKeyStatus, ignoreCase: true),
            CreatedAt = ApiKeyCreatedAt,
            ExpiresAt = ApiKeyExpiresAt,
            LastUsedAt = ApiKeyLastUsedAt,
            Description = ApiKeyDescription,
        };

        public Tenant ToTenant() => new()
        {
            Id = TenantId,
            Slug = TenantSlug,
            Name = TenantName,
            Status = Enum.Parse<OperationalStatus>(TenantStatus, ignoreCase: true),
            Environment = TenantEnvironment,
            CreatedAt = TenantCreatedAt,
            UpdatedAt = TenantUpdatedAt,
            Description = TenantDescription,
        };
    }
}
