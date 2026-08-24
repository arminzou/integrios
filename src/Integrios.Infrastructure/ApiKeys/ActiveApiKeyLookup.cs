using Dapper;
using Integrios.Application.ApiKeys;
using Integrios.Infrastructure.Data;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Infrastructure.ApiKeys;

internal sealed class ActiveApiKeyLookup(IDbConnectionFactory connectionFactory)
    : IActiveApiKeyLookup
{
    public async Task<(ApiKey ApiKey, Tenant Tenant)?> FindActiveByKeyHashAsync(
        string keyHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        string currentTimestamp = connectionFactory.Provider == DatabaseProvider.SqlServer
            ? "SYSUTCDATETIME()"
            : "now()";
        string sql = $"""
            SELECT
                c.id           AS ApiKeyId,
                c.tenant_id    AS ApiKeyTenantId,
                c.name         AS ApiKeyName,
                c.key_prefix   AS ApiKeyKeyPrefix,
                c.key_hash     AS ApiKeyKeyHash,
                c.status       AS ApiKeyStatus,
                c.created_at   AS ApiKeyCreatedAt,
                c.expires_at   AS ApiKeyExpiresAt,
                c.last_used_at AS ApiKeyLastUsedAt,
                c.revoked_at   AS ApiKeyRevokedAt,
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
            WHERE c.key_hash = @KeyHash
              AND c.status = 'active'
              AND t.status = 'active'
              AND (c.expires_at IS NULL OR c.expires_at > {currentTimestamp})
            """;

        ApiKeyTenantRow? row = await connection.QuerySingleOrDefaultAsync<ApiKeyTenantRow>(
            new CommandDefinition(sql, new { KeyHash = keyHash }, cancellationToken: cancellationToken));

        return row is null ? null : (row.ToApiKey(), row.ToTenant());
    }

    private sealed record ApiKeyTenantRow
    {
        public Guid ApiKeyId { get; init; }
        public Guid ApiKeyTenantId { get; init; }
        public string ApiKeyName { get; init; } = "";
        public string ApiKeyKeyPrefix { get; init; } = "";
        public string ApiKeyKeyHash { get; init; } = "";
        public string ApiKeyStatus { get; init; } = "";
        public DateTimeOffset ApiKeyCreatedAt { get; init; }
        public DateTimeOffset? ApiKeyExpiresAt { get; init; }
        public DateTimeOffset? ApiKeyLastUsedAt { get; init; }
        public DateTimeOffset? ApiKeyRevokedAt { get; init; }
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
            KeyPrefix = ApiKeyKeyPrefix,
            KeyHash = ApiKeyKeyHash,
            Status = Enum.Parse<OperationalStatus>(ApiKeyStatus, ignoreCase: true),
            CreatedAt = ApiKeyCreatedAt,
            ExpiresAt = ApiKeyExpiresAt,
            LastUsedAt = ApiKeyLastUsedAt,
            RevokedAt = ApiKeyRevokedAt,
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
