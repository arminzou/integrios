using Dapper;
using Integrios.Application.Abstractions;
using Integrios.Domain.Tenants;

namespace Integrios.Infrastructure.Data;

public sealed class AdminKeyRepository(IDbConnectionFactory connectionFactory) : IAdminKeyRepository
{
    public async Task<AdminKey?> FindActiveByPublicKeyAsync(
        string publicKey, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                id         AS Id,
                tenant_id  AS TenantId,
                public_key AS PublicKey,
                secret_hash AS SecretHash,
                name       AS Name,
                created_at AS CreatedAt,
                revoked_at AS RevokedAt
            FROM admin_keys
            WHERE public_key = @PublicKey
              AND revoked_at IS NULL
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AdminKeyRow>(sql, new { PublicKey = publicKey });
        return row?.ToAdminKey();
    }

    private sealed record AdminKeyRow
    {
        public Guid Id { get; init; }
        public Guid? TenantId { get; init; }
        public string PublicKey { get; init; } = "";
        public string SecretHash { get; init; } = "";
        public string Name { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }

        public AdminKey ToAdminKey() => new()
        {
            Id = Id,
            TenantId = TenantId,
            PublicKey = PublicKey,
            SecretHash = SecretHash,
            Name = Name,
            CreatedAt = CreatedAt,
            RevokedAt = RevokedAt,
        };
    }
}
