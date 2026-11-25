using Dapper;
using Integrios.Application.PullDelivery;
using Integrios.Infrastructure.Connectors;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.SubscriberKeys;

internal sealed class ActiveSubscriberKeyLookup(IDbConnectionFactory connectionFactory) : IActiveSubscriberKeyLookup
{
    public async Task<SubscriberIdentity?> FindActiveByKeyHashAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT k.id AS KeyId, k.tenant_id AS TenantId, k.destination_id AS DestinationId,
                   k.last_used_at AS LastUsedAt
            FROM subscriber_keys k
            JOIN tenants t ON t.id = k.tenant_id
            JOIN destinations d ON d.id = k.destination_id AND d.tenant_id = k.tenant_id
            WHERE k.key_hash = @KeyHash AND k.revoked_at IS NULL AND k.deleted_at IS NULL
              AND t.status = 'active'
              AND d.status = 'active' AND d.deleted_at IS NULL
              AND d.connector_id = @PullConnectorId
            """;
        return await connection.QuerySingleOrDefaultAsync<SubscriberIdentity>(
            new CommandDefinition(sql, new { KeyHash = keyHash, PullConnectorId = BuiltInConnectors.Pull.Id }, cancellationToken: cancellationToken));
    }
}
