using Integrios.Application.Ingestion;
using MediatR;

namespace Integrios.Application.Delivery;

public sealed record GetTenantEventActivitySummaryQuery(Guid TenantId, Guid? SourceId, Guid? TopicId)
    : IRequest<EventActivitySummaryDto>;

public sealed record EventActivitySummaryDto(
    int EventsAccepted,
    int AwaitingRouting,
    int Unrouted,
    int DeadLetteredDeliveries,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);

internal sealed class GetTenantEventActivitySummaryQueryHandler(ITenantEventActivitySummary activitySummary)
    : IRequestHandler<GetTenantEventActivitySummaryQuery, EventActivitySummaryDto>
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(60);

    public async Task<EventActivitySummaryDto> Handle(
        GetTenantEventActivitySummaryQuery query, CancellationToken cancellationToken)
    {
        DateTimeOffset windowEnd = DateTimeOffset.UtcNow;
        DateTimeOffset windowStart = windowEnd - WindowLength;
        var filter = new TenantEventActivityFilter(query.SourceId, query.TopicId);
        EventActivitySummaryCounts counts = await activitySummary.GetAsync(
            query.TenantId, filter, windowStart, windowEnd, cancellationToken);
        return new EventActivitySummaryDto(
            counts.EventsAccepted, counts.AwaitingRouting, counts.Unrouted, counts.DeadLetteredDeliveries,
            windowStart, windowEnd);
    }
}
