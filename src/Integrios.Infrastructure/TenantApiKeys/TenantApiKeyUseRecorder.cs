using Dapper;
using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.TenantApiKeys;

internal sealed class TenantApiKeyUseRecorder(IDbConnectionFactory connectionFactory) : ITenantApiKeyUseRecorder
{
    /// <summary>
    /// The resolution is repeated in the WHERE clause rather than trusted from the caller's own
    /// check. Two requests carrying the same key can decide to record at the same instant; letting
    /// the statement match only a row that is still stale makes the second one a no-op instead of a
    /// second write, and makes the whole thing idempotent under retry.
    /// </summary>
    public async Task RecordUseAsync(Guid tenantApiKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE tenant_api_keys
            SET last_used_at = @UsedAt
            WHERE id = @Id
              AND (last_used_at IS NULL OR last_used_at < @Stale)
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = tenantApiKeyId, UsedAt = usedAt, Stale = usedAt - TenantApiKeyUse.Resolution },
            cancellationToken: cancellationToken));
    }
}
