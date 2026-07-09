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

    // Bootstrap: does a live global admin key already exist?
    public async Task<bool> HasLiveGlobalKeyAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM admin_keys WHERE tenant_id IS NULL AND revoked_at IS NULL
            )
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<bool>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    // Bootstrap: insert the first admin key row
    public async Task<AdminKey> InsertAsync(AdminKey adminKey, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO admin_keys (id, tenant_id, public_key, secret_hash, name, created_at)
            VALUES (@Id, @TenantId, @PublicKey, @SecretHash, @Name, @CreatedAt)
            RETURNING
                id         AS Id,
                tenant_id  AS TenantId,
                public_key AS PublicKey,
                secret_hash AS SecretHash,
                name       AS Name,
                created_at AS CreatedAt,
                revoked_at AS RevokedAt
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        AdminKeyRow row = await connection.QuerySingleAsync<AdminKeyRow>(new CommandDefinition(
            sql,
            new
            {
                adminKey.Id,
                adminKey.TenantId,
                adminKey.PublicKey,
                adminKey.SecretHash,
                adminKey.Name,
                adminKey.CreatedAt,
            },
            cancellationToken: cancellationToken));
        return row.ToAdminKey();
    }

    // Rotate the live global admin key: revoke the current one (if any), insert newKey
    public async Task<AdminKey> RotateGlobalAsync(AdminKey newKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string revokeSql = """
            UPDATE admin_keys SET revoked_at = now()
            WHERE tenant_id IS NULL AND revoked_at IS NULL
            """;
        await connection.ExecuteAsync(new CommandDefinition(revokeSql, transaction: transaction, cancellationToken: cancellationToken));

        const string insertSql = """
            INSERT INTO admin_keys (id, tenant_id, public_key, secret_hash, name, created_at)
            VALUES (@Id, NULL, @PublicKey, @SecretHash, @Name, @CreatedAt)
            RETURNING
                id         AS Id,
                tenant_id  AS TenantId,
                public_key AS PublicKey,
                secret_hash AS SecretHash,
                name       AS Name,
                created_at AS CreatedAt,
                revoked_at AS RevokedAt
            """;
        AdminKeyRow row = await connection.QuerySingleAsync<AdminKeyRow>(new CommandDefinition(
            insertSql,
            new
            {
                newKey.Id,
                newKey.PublicKey,
                newKey.SecretHash,
                newKey.Name,
                newKey.CreatedAt,
            },
            transaction: transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return row.ToAdminKey();
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
