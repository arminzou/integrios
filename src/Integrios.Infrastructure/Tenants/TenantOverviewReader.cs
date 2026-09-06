using Dapper;
using Integrios.Application.Authoring.Tenants;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Tenants;

internal sealed class TenantOverviewReader(IDbConnectionFactory connectionFactory) : ITenantOverview
{
    public async Task<TenantOverviewCounts> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        // Five scalar subqueries in one statement rather than five round trips: each is a covered
        // count over one Tenant's rows, and the screen shows them together or not at all.
        //
        // Subscriptions are counted through their Topic because that is where the API says they
        // live; counting subscriptions.tenant_id directly would agree today and drift the moment
        // ownership moves.
        //
        // "Live" API keys are the ones a caller could still authenticate with. A revoked key stays
        // in the table as configuration history and is deliberately not counted here.
        //
        // COUNT(*) returns bigint, so every count is cast down to the int the DTO carries.
        const string sql = """
            SELECT
                (SELECT CAST(COUNT(*) AS INT) FROM topics WHERE tenant_id = @TenantId) AS Topics,
                (SELECT CAST(COUNT(*) AS INT) FROM connections WHERE tenant_id = @TenantId) AS Connections,
                (SELECT CAST(COUNT(*) AS INT) FROM sources WHERE tenant_id = @TenantId) AS Sources,
                (SELECT CAST(COUNT(*) AS INT) FROM subscriptions s
                    JOIN topics t ON t.id = s.topic_id
                    WHERE t.tenant_id = @TenantId) AS Subscriptions,
                (SELECT CAST(COUNT(*) AS INT) FROM tenant_api_keys
                    WHERE tenant_id = @TenantId AND status = 'active') AS LiveApiKeys
            """;

        return await connection.QuerySingleAsync<TenantOverviewCounts>(
            new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }
}
