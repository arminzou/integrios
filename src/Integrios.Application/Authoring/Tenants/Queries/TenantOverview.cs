using MediatR;

namespace Integrios.Application.Authoring.Tenants;

/// <summary>
/// What a Tenant currently has configured, and where its Events are sent.
/// </summary>
/// <remarks>
/// Configuration counts, plus the one piece of runtime state that is not a measure of activity.
/// Nothing here counts Events: the ledger is a cursor list with no total, and reporting one on this
/// screen would make the two disagree about what a number means.
/// </remarks>
public sealed record TenantOverviewDto
{
    public required int Topics { get; init; }
    public required int Connections { get; init; }
    public required int Sources { get; init; }
    public required int Subscriptions { get; init; }

    /// <summary>Keys a caller can still authenticate with — a revoked key is configuration history.</summary>
    public required int LiveApiKeys { get; init; }

    /// <summary>
    /// EventDeliveries that have exhausted their retry budget and are still waiting for an Operator.
    /// Deliberately not windowed, and deliberately not the same number as the activity summary's
    /// dead-lettered count: that one measures what failed inside the last hour, while this is
    /// outstanding work that does not age out. A Delivery that dead-lettered yesterday is still
    /// broken today, so anything offering to take an Operator to it has to count it.
    /// </summary>
    public required int DeadLetteredDeliveries { get; init; }

    /// <summary>
    /// Where this deployment accepts Events. Deployment-wide rather than per Tenant, and carried here
    /// because this is the screen an Operator is on when they need to hand it to whoever is sending.
    /// </summary>
    public string? IngestionEndpoint { get; init; }
}

public interface ITenantOverview
{
    Task<TenantOverviewCounts> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record TenantOverviewCounts(
    int Topics,
    int Connections,
    int Sources,
    int Subscriptions,
    int LiveApiKeys,
    int DeadLetteredDeliveries);

/// <remarks>
/// Answers null when the Tenant does not exist, so the endpoint can 404 rather than report a Tenant
/// with nothing configured — an absent Tenant and an empty one are different answers.
/// </remarks>
public sealed record GetTenantOverviewQuery(Guid TenantId, string? IngestionEndpoint)
    : IRequest<TenantOverviewDto?>;

internal sealed class GetTenantOverviewQueryHandler(ITenantRepository repository, ITenantOverview overview)
    : IRequestHandler<GetTenantOverviewQuery, TenantOverviewDto?>
{
    public async Task<TenantOverviewDto?> Handle(GetTenantOverviewQuery query, CancellationToken cancellationToken)
    {
        if (await repository.GetByIdAsync(query.TenantId, cancellationToken) is null)
            return null;

        TenantOverviewCounts counts = await overview.GetAsync(query.TenantId, cancellationToken);
        return new TenantOverviewDto
        {
            Topics = counts.Topics,
            Connections = counts.Connections,
            Sources = counts.Sources,
            Subscriptions = counts.Subscriptions,
            LiveApiKeys = counts.LiveApiKeys,
            DeadLetteredDeliveries = counts.DeadLetteredDeliveries,
            IngestionEndpoint = query.IngestionEndpoint,
        };
    }
}
