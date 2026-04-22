using Dapper;
using Integrios.Core.Domain.Common;
using Integrios.Core.Domain.Tenants;

namespace Integrios.Api.Infrastructure.Data.Tenants;

public sealed class ApiKeyRepository(IDbConnectionFactory connectionFactory) : IApiKeyRepository
{
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
        var row = await connection.QuerySingleOrDefaultAsync<ApiKeyRow>(sql, new { PublicKey = publicKey });
        if (row is null) return null;

        return (row.ToApiKey(), row.ToTenant());
    }

    private sealed record ApiKeyRow
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
