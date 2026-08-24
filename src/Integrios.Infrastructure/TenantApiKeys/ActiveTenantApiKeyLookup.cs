using Dapper;
using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Infrastructure.Data;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Infrastructure.TenantApiKeys;

internal sealed class ActiveTenantApiKeyLookup(IDbConnectionFactory connectionFactory)
    : IActiveTenantApiKeyLookup
{
    public async Task<(TenantApiKey TenantApiKey, Tenant Tenant)?> FindActiveByKeyHashAsync(
        string keyHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        string currentTimestamp = connectionFactory.Provider == DatabaseProvider.SqlServer
            ? "SYSUTCDATETIME()"
            : "now()";
        string sql = $"""
            SELECT
                c.id           AS TenantApiKeyId,
                c.tenant_id    AS TenantApiKeyTenantId,
                c.name         AS TenantApiKeyName,
                c.key_prefix   AS TenantApiKeyKeyPrefix,
                c.key_hash     AS TenantApiKeyKeyHash,
                c.status       AS TenantApiKeyStatus,
                c.created_at   AS TenantApiKeyCreatedAt,
                c.expires_at   AS TenantApiKeyExpiresAt,
                c.last_used_at AS TenantApiKeyLastUsedAt,
                c.revoked_at   AS TenantApiKeyRevokedAt,
                c.description  AS TenantApiKeyDescription,
                t.id           AS TenantId,
                t.slug         AS TenantSlug,
                t.name         AS TenantName,
                t.status       AS TenantStatus,
                t.environment  AS TenantEnvironment,
                t.created_at   AS TenantCreatedAt,
                t.updated_at   AS TenantUpdatedAt,
                t.description  AS TenantDescription
            FROM tenant_api_keys c
            JOIN tenants t ON t.id = c.tenant_id
            WHERE c.key_hash = @KeyHash
              AND c.status = 'active'
              AND t.status = 'active'
              AND (c.expires_at IS NULL OR c.expires_at > {currentTimestamp})
            """;

        TenantApiKeyTenantRow? row = await connection.QuerySingleOrDefaultAsync<TenantApiKeyTenantRow>(
            new CommandDefinition(sql, new { KeyHash = keyHash }, cancellationToken: cancellationToken));

        return row is null ? null : (row.ToTenantApiKey(), row.ToTenant());
    }

    private sealed record TenantApiKeyTenantRow
    {
        public Guid TenantApiKeyId { get; init; }
        public Guid TenantApiKeyTenantId { get; init; }
        public string TenantApiKeyName { get; init; } = "";
        public string TenantApiKeyKeyPrefix { get; init; } = "";
        public string TenantApiKeyKeyHash { get; init; } = "";
        public string TenantApiKeyStatus { get; init; } = "";
        public DateTimeOffset TenantApiKeyCreatedAt { get; init; }
        public DateTimeOffset? TenantApiKeyExpiresAt { get; init; }
        public DateTimeOffset? TenantApiKeyLastUsedAt { get; init; }
        public DateTimeOffset? TenantApiKeyRevokedAt { get; init; }
        public string? TenantApiKeyDescription { get; init; }
        public Guid TenantId { get; init; }
        public string TenantSlug { get; init; } = "";
        public string TenantName { get; init; } = "";
        public string TenantStatus { get; init; } = "";
        public string? TenantEnvironment { get; init; }
        public DateTimeOffset TenantCreatedAt { get; init; }
        public DateTimeOffset TenantUpdatedAt { get; init; }
        public string? TenantDescription { get; init; }

        public TenantApiKey ToTenantApiKey() => new()
        {
            Id = TenantApiKeyId,
            TenantId = TenantApiKeyTenantId,
            Name = TenantApiKeyName,
            KeyPrefix = TenantApiKeyKeyPrefix,
            KeyHash = TenantApiKeyKeyHash,
            Status = Enum.Parse<OperationalStatus>(TenantApiKeyStatus, ignoreCase: true),
            CreatedAt = TenantApiKeyCreatedAt,
            ExpiresAt = TenantApiKeyExpiresAt,
            LastUsedAt = TenantApiKeyLastUsedAt,
            RevokedAt = TenantApiKeyRevokedAt,
            Description = TenantApiKeyDescription,
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
