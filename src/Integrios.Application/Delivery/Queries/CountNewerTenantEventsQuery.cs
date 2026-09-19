using Integrios.Application.Ingestion;
using MediatR;

namespace Integrios.Application.Delivery;

public sealed record CountNewerTenantEventsQuery(Guid TenantId, TenantEventFilter Filter, string Watermark)
    : IRequest<EventFreshnessDto>;

/// Capped means at least Count Events arrived; the exact number past the cap is not worth a scan.
public sealed record EventFreshnessDto(int Count, bool Capped);

internal sealed class CountNewerTenantEventsQueryHandler(ITenantEventHistory eventHistory)
    : IRequestHandler<CountNewerTenantEventsQuery, EventFreshnessDto>
{
    private const int Cap = 100;

    public async Task<EventFreshnessDto> Handle(CountNewerTenantEventsQuery query, CancellationToken cancellationToken)
    {
        int count = await eventHistory.CountNewerAsync(
            query.TenantId, query.Filter, query.Watermark, Cap + 1, cancellationToken);
        return new EventFreshnessDto(Math.Min(count, Cap), count > Cap);
    }
}
