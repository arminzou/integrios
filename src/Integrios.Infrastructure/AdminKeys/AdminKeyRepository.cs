using Dapper;
using Integrios.Application.AdminKeys;
using Integrios.Domain.Tenants;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.AdminKeys;

internal sealed class AdminKeyRepository(IDbConnectionFactory connectionFactory)
    : IAdminKeyLookup, IAdminKeyLifecycle
{
    public async Task<AdminKey?> FindActiveByPublicKeyAsync(
        string publicKey, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id         AS Id,
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

    // Bootstrap: does a live deployment-wide admin key already exist?
    public async Task<bool> HasLiveKeyAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM admin_keys WHERE revoked_at IS NULL
            )
            """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<bool>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    // Bootstrap: insert the first admin key row
    public async Task<AdminKey> InsertAsync(AdminKey adminKey, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO admin_keys (id, public_key, secret_hash, name, created_at)
            VALUES (@Id, @PublicKey, @SecretHash, @Name, @CreatedAt)
            RETURNING
                id         AS Id,
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
                adminKey.PublicKey,
                adminKey.SecretHash,
                adminKey.Name,
                adminKey.CreatedAt,
            },
            cancellationToken: cancellationToken));
        return row.ToAdminKey();
    }

    // Rotate the live deployment-wide admin key: revoke the current one (if any), insert newKey.
    public async Task<AdminKey> RotateAsync(AdminKey newKey, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize concurrent rotations so they cannot leave two live keys.
        await connection.ExecuteAsync(new CommandDefinition(
            "LOCK TABLE admin_keys IN SHARE ROW EXCLUSIVE MODE",
            transaction: transaction,
            cancellationToken: cancellationToken));

        const string revokeSql = """
            UPDATE admin_keys SET revoked_at = now()
            WHERE revoked_at IS NULL
            """;
        int revoked = await connection.ExecuteAsync(new CommandDefinition(
            revokeSql,
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (revoked == 0)
            throw new InvalidOperationException("No live AdminKey exists. Run bootstrap before rotation.");

        const string insertSql = """
            INSERT INTO admin_keys (id, public_key, secret_hash, name, created_at)
            VALUES (@Id, @PublicKey, @SecretHash, @Name, @CreatedAt)
            RETURNING
                id         AS Id,
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
        public string PublicKey { get; init; } = "";
        public string SecretHash { get; init; } = "";
        public string Name { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }

        public AdminKey ToAdminKey() => new()
        {
            Id = Id,
            PublicKey = PublicKey,
            SecretHash = SecretHash,
            Name = Name,
            CreatedAt = CreatedAt,
            RevokedAt = RevokedAt,
        };
    }
}
