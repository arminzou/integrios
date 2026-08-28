using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Infrastructure.UnitTests;

public sealed class EventDeliveryClaimabilityTests
{
    [Theory]
    [InlineData("postgres", "now()", "sd.status = 'in_flight'", "sd.status = 'pending'")]
    [InlineData("sqlserver", "SYSUTCDATETIME()", "sd.status=N'in_flight'", "sd.status=N'pending'")]
    public void Predicate_MatchesBothClaimableNowPaths(
        string provider,
        string databaseNow,
        string recoveredLease,
        string pendingDelivery)
    {
        string predicate = EventDeliveryClaimability.Predicate(ParseProvider(provider), "sd", databaseNow);

        predicate.ShouldContain(recoveredLease);
        predicate.ShouldContain(pendingDelivery);
        predicate.ShouldContain($"lease_expires_at <= {databaseNow}");
        predicate.ShouldContain($"deliver_after <= {databaseNow}");
    }

    [Theory]
    [InlineData("postgres", "sd.status = 'in_flight'")]
    [InlineData("sqlserver", "sd.status=N'in_flight'")]
    public void EligibilityAnchor_UsesLeaseExpiryOrScheduledEligibility(
        string provider,
        string recoveredLease)
    {
        string anchor = EventDeliveryClaimability.EligibilityAnchor(ParseProvider(provider), "sd");

        anchor.ShouldContain(recoveredLease);
        anchor.ShouldContain("lease_expires_at");
        anchor.ShouldContain("COALESCE(sd.deliver_after, sd.created_at)");
    }

    private static DatabaseProvider ParseProvider(string provider) => provider switch
    {
        "postgres" => DatabaseProvider.Postgres,
        "sqlserver" => DatabaseProvider.SqlServer,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
