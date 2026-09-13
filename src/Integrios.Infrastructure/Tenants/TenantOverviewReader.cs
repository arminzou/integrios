using Dapper;
using Integrios.Application.Authoring.Tenants;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Tenants;

internal sealed class TenantOverviewReader(IDbConnectionFactory connectionFactory) : ITenantOverview
{
    public async Task<TenantOverviewCounts> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        // Scalar subqueries in one statement rather than one round trip each: every one is a covered
        // count over a single Tenant's rows, and the screen shows them together or not at all.
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
                (SELECT CAST(COUNT(*) AS INT) FROM destinations WHERE tenant_id = @TenantId) AS Destinations,
                (SELECT CAST(COUNT(*) AS INT) FROM sources WHERE tenant_id = @TenantId) AS Sources,
                (SELECT CAST(COUNT(*) AS INT) FROM subscriptions s
                    JOIN topics t ON t.id = s.topic_id
                    WHERE t.tenant_id = @TenantId) AS Subscriptions,
                (SELECT CAST(COUNT(*) AS INT) FROM tenant_api_keys
                    WHERE tenant_id = @TenantId AND status = 'active') AS LiveApiKeys,
                -- Outstanding rather than recent: a dead-lettered Delivery stays dead-lettered until
                -- an Operator replays it, so this is not windowed the way the activity summary is.
                -- Driven from this Tenant's Events and probing the Delivery index per row, which is
                -- the same shape the activity summary settled on for the same reason.
                (SELECT CAST(COUNT(*) AS INT) FROM event_deliveries d
                    JOIN events e ON e.id = d.event_id
                    WHERE e.tenant_id = @TenantId AND d.status = 'dead_lettered') AS DeadLetteredDeliveries
            """;

        return await connection.QuerySingleAsync<TenantOverviewCounts>(
            new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }
}
