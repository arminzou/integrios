using Integrios.Application.Common.Exceptions;
using Integrios.Application.Delivery;
using Integrios.Application.Ingestion;
using Integrios.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Integrios.Admin.Endpoints;

public sealed class EventsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/events";

    public void Map(RouteGroupBuilder group)
    {
        // The dashboard reads this list, so it declares its schema; see DashboardResponseSchemaTests.
        group.MapGet(ListTenantEvents).Produces<EventListDto>();
        group.MapGet(GetActivity, "/activity").Produces<EventActivityDto>();
        group.MapGet(GetBacklog, "/backlog").Produces<EventBacklogDto>();
        group.MapGet(CountNewerEvents, "/freshness").Produces<EventFreshnessDto>();
    }

    private static async Task<IResult> ListTenantEvents(
        Guid tenantId,
        IMediator mediator,
        [AsParameters] TenantEventFilterRequest filter,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        EventListDto response = await mediator.Send(
            new ListTenantEventsQuery(tenantId, filter.ToFilter(), after, limit), cancellationToken);
        return Results.Ok(response);
    }

    // The same filters as the ledger, so the count can only ever describe rows that ledger would show.
    private static async Task<IResult> CountNewerEvents(
        Guid tenantId,
        IMediator mediator,
        [AsParameters] TenantEventFilterRequest filter,
        string? watermark,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(watermark))
            throw new InvalidListFilterException("watermark is required.");
        EventFreshnessDto response = await mediator.Send(
            new CountNewerTenantEventsQuery(tenantId, filter.ToFilter(), watermark), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetActivity(
        Guid tenantId, IMediator mediator, string? range, CancellationToken cancellationToken) =>
        Results.Ok(await mediator.Send(new GetTenantEventActivityQuery(tenantId, range), cancellationToken));

    private static async Task<IResult> GetBacklog(Guid tenantId, IMediator mediator, CancellationToken cancellationToken) =>
        Results.Ok(await mediator.Send(new GetTenantEventBacklogQuery(tenantId), cancellationToken));
}

/// The Event-history filters as the wire spells them. Untrusted query values become a trusted
/// TenantEventFilter here and nowhere else, for the ledger and its freshness count alike.
public sealed record TenantEventFilterRequest(
    [property: FromQuery(Name = "status")] string? Status,
    [property: FromQuery(Name = "delivery_status")] string? DeliveryStatus,
    [property: FromQuery(Name = "source_id")] Guid? SourceId,
    [property: FromQuery(Name = "topic_id")] Guid? TopicId,
    [property: FromQuery(Name = "source_event_id")] string? SourceEventId,
    [property: FromQuery(Name = "event_type")] string? EventType,
    [property: FromQuery(Name = "accepted_from")] DateTimeOffset? AcceptedFrom,
    [property: FromQuery(Name = "accepted_to")] DateTimeOffset? AcceptedTo)
{
    private static readonly string[] DeliveryStatuses = ["pending", "in_flight", "succeeded", "dead_lettered"];

    public TenantEventFilter ToFilter()
    {
        if (DeliveryStatus is not null && !DeliveryStatuses.Contains(DeliveryStatus))
            throw new InvalidListFilterException("Delivery status must be pending, in_flight, succeeded, or dead_lettered.");
        if (AcceptedFrom > AcceptedTo)
            throw new InvalidListFilterException("accepted_from must not be later than accepted_to.");
        return new TenantEventFilter(
            ParseEventStatus(Status), DeliveryStatus, SourceId, TopicId,
            string.IsNullOrEmpty(SourceEventId) ? null : SourceEventId, AcceptedFrom, AcceptedTo,
            string.IsNullOrEmpty(EventType) ? null : EventType);
    }

    private static EventStatus? ParseEventStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return null;
        try
        { return EventStatusMap.FromDbValue(status); }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidListFilterException("Event status must be accepted, processing, routed, unrouted, failed, or dead_lettered.");
        }
    }
}
