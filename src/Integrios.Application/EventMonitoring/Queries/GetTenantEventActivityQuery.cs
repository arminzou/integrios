using Integrios.Application.Common.Exceptions;
using MediatR;

namespace Integrios.Application.EventMonitoring;

public sealed record GetTenantEventActivityQuery(Guid TenantId, string? Range) : IRequest<EventActivityDto>;

/// Buckets are ordered, zero-filled, and half-open: an Event accepted exactly on a boundary belongs
/// to the later bucket only. WindowEnd is the instant the read was taken, whole-second aligned.
public sealed record EventActivityDto(
    string Range,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<EventActivityBucketDto> Buckets);

public sealed record EventActivityBucketDto(
    DateTimeOffset Start,
    DateTimeOffset End,
    int AwaitingRouting,
    int Unrouted,
    int DeliveryDeadLettered,
    int Routed);

internal sealed class GetTenantEventActivityQueryHandler(ITenantEventMonitoring monitoring)
    : IRequestHandler<GetTenantEventActivityQuery, EventActivityDto>
{
    // Fixed so a bar always means the same span and the chart never needs more than 28 of them.
    private static readonly Dictionary<string, (TimeSpan Bucket, int Count)> Ranges = new()
    {
        ["1h"] = (TimeSpan.FromMinutes(5), 12),
        ["24h"] = (TimeSpan.FromHours(1), 24),
        ["7d"] = (TimeSpan.FromHours(6), 28),
    };

    public async Task<EventActivityDto> Handle(GetTenantEventActivityQuery query, CancellationToken cancellationToken)
    {
        string range = query.Range ?? "1h";
        if (!Ranges.TryGetValue(range, out var shape))
            throw new InvalidListFilterException("range must be 1h, 24h, or 7d.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var windowEnd = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        DateTimeOffset windowStart = windowEnd - shape.Bucket * shape.Count;

        IReadOnlyList<EventActivityBucketCounts> counted = await monitoring.GetActivityAsync(
            query.TenantId, windowStart, shape.Bucket, shape.Count, cancellationToken);
        Dictionary<int, EventActivityBucketCounts> byIndex = counted.ToDictionary(bucket => bucket.BucketIndex);

        var buckets = new List<EventActivityBucketDto>(shape.Count);
        for (int index = 0; index < shape.Count; index++)
        {
            DateTimeOffset start = windowStart + shape.Bucket * index;
            byIndex.TryGetValue(index, out EventActivityBucketCounts? counts);
            buckets.Add(new EventActivityBucketDto(
                start, start + shape.Bucket,
                counts?.AwaitingRouting ?? 0, counts?.Unrouted ?? 0, counts?.DeliveryDeadLettered ?? 0, counts?.Routed ?? 0));
        }
        return new EventActivityDto(range, windowStart, windowEnd, buckets);
    }
}
