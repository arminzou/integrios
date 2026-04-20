using Dapper;
using Integrios.Core.Domain.Common;
using Integrios.Core.Domain.Tenants;

namespace Integrios.Api.Infrastructure.Data.Tenants;

public sealed class ApiCredentialRepository(IDbConnectionFactory connectionFactory) : IApiCredentialRepository
{
    public async Task<(ApiCredential Credential, Tenant Tenant)?> FindActiveByKeyIdAsync(
        string keyId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                c.id           AS CredId,
                c.tenant_id    AS CredTenantId,
                c.name         AS CredName,
                c.key_id       AS CredKeyId,
                c.secret_hash  AS CredSecretHash,
                c.scopes       AS CredScopes,
                c.status       AS CredStatus,
                c.created_at   AS CredCreatedAt,
                c.expires_at   AS CredExpiresAt,
                c.last_used_at AS CredLastUsedAt,
                c.description  AS CredDescription,
                t.id           AS TenId,
                t.slug         AS TenSlug,
                t.name         AS TenName,
                t.status       AS TenStatus,
                t.environment  AS TenEnvironment,
                t.created_at   AS TenCreatedAt,
                t.updated_at   AS TenUpdatedAt,
                t.description  AS TenDescription
            FROM api_credentials c
            JOIN tenants t ON t.id = c.tenant_id
            WHERE c.key_id = @KeyId
              AND c.status = 'active'
              AND t.status = 'active'
              AND (c.expires_at IS NULL OR c.expires_at > now())
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CredentialRow>(sql, new { KeyId = keyId });
        if (row is null) return null;

        return (row.ToCredential(), row.ToTenant());
    }

    private sealed record CredentialRow
    {
        public Guid CredId { get; init; }
        public Guid CredTenantId { get; init; }
        public string CredName { get; init; } = "";
        public string CredKeyId { get; init; } = "";
        public string CredSecretHash { get; init; } = "";
        public string[] CredScopes { get; init; } = [];
        public string CredStatus { get; init; } = "";
        public DateTimeOffset CredCreatedAt { get; init; }
        public DateTimeOffset? CredExpiresAt { get; init; }
        public DateTimeOffset? CredLastUsedAt { get; init; }
        public string? CredDescription { get; init; }
        public Guid TenId { get; init; }
        public string TenSlug { get; init; } = "";
        public string TenName { get; init; } = "";
        public string TenStatus { get; init; } = "";
        public string? TenEnvironment { get; init; }
        public DateTimeOffset TenCreatedAt { get; init; }
        public DateTimeOffset TenUpdatedAt { get; init; }
        public string? TenDescription { get; init; }

        public ApiCredential ToCredential() => new()
        {
            Id = CredId,
            TenantId = CredTenantId,
            Name = CredName,
            KeyId = CredKeyId,
            SecretHash = CredSecretHash,
            Scopes = CredScopes,
            Status = Enum.Parse<OperationalStatus>(CredStatus, ignoreCase: true),
            CreatedAt = CredCreatedAt,
            ExpiresAt = CredExpiresAt,
            LastUsedAt = CredLastUsedAt,
            Description = CredDescription,
        };

        public Tenant ToTenant() => new()
        {
            Id = TenId,
            Slug = TenSlug,
            Name = TenName,
            Status = Enum.Parse<OperationalStatus>(TenStatus, ignoreCase: true),
            Environment = TenEnvironment,
            CreatedAt = TenCreatedAt,
            UpdatedAt = TenUpdatedAt,
            Description = TenDescription,
        };
    }
}
