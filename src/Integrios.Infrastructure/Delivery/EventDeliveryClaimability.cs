using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Delivery;

internal static class EventDeliveryClaimability
{
    public static string Predicate(DatabaseProvider provider, string alias, string databaseNow) => provider switch
    {
        DatabaseProvider.SqlServer =>
            $"({alias}.status=N'in_flight' AND {alias}.lease_expires_at <= {databaseNow})"
            + $" OR ({alias}.status=N'pending' AND ({alias}.deliver_after IS NULL OR {alias}.deliver_after <= {databaseNow}))",
        DatabaseProvider.Postgres =>
            $"({alias}.status = 'in_flight' AND {alias}.lease_expires_at <= {databaseNow})"
            + $" OR ({alias}.status = 'pending' AND ({alias}.deliver_after IS NULL OR {alias}.deliver_after <= {databaseNow}))",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    public static string EligibilityAnchor(DatabaseProvider provider, string alias) => provider switch
    {
        DatabaseProvider.SqlServer =>
            $"CASE WHEN {alias}.status=N'in_flight' THEN {alias}.lease_expires_at ELSE COALESCE({alias}.deliver_after, {alias}.created_at) END",
        DatabaseProvider.Postgres =>
            $"CASE WHEN {alias}.status = 'in_flight' THEN {alias}.lease_expires_at ELSE COALESCE({alias}.deliver_after, {alias}.created_at) END",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
