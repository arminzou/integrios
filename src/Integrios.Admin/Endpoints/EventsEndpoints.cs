using Integrios.Application.Common.Exceptions;
using Integrios.Application.Delivery;
using Integrios.Application.Ingestion;
using Integrios.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Integrios.Admin.Endpoints;

public sealed class EventsEndpoints : IEndpointGroup
{
    private static readonly string[] DeliveryStatuses = ["pending", "in_flight", "succeeded", "dead_lettered"];

    public string Prefix => "/tenants/{tenantId:guid}/events";

    public void Map(RouteGroupBuilder group)
    {
        // The dashboard reads this list, so it declares its schema; see DashboardResponseSchemaTests.
        group.MapGet(ListTenantEvents).Produces<EventListDto>();
    }

    private static async Task<IResult> ListTenantEvents(
        Guid tenantId,
        IMediator mediator,
        string? status,
        [FromQuery(Name = "delivery_status")] string? deliveryStatus,
        [FromQuery(Name = "source_id")] Guid? sourceId,
        [FromQuery(Name = "topic_id")] Guid? topicId,
        [FromQuery(Name = "source_event_id")] string? sourceEventId,
        [FromQuery(Name = "accepted_from")] DateTimeOffset? acceptedFrom,
        [FromQuery(Name = "accepted_to")] DateTimeOffset? acceptedTo,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        if (deliveryStatus is not null && !DeliveryStatuses.Contains(deliveryStatus))
            throw new InvalidListFilterException("Delivery status must be pending, in_flight, succeeded, or dead_lettered.");
        if (acceptedFrom > acceptedTo)
            throw new InvalidListFilterException("accepted_from must not be later than accepted_to.");

        var filter = new TenantEventFilter(
            ParseEventStatus(status), deliveryStatus, sourceId, topicId,
            string.IsNullOrEmpty(sourceEventId) ? null : sourceEventId, acceptedFrom, acceptedTo);
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        EventListDto response = await mediator.Send(
            new ListTenantEventsQuery(tenantId, filter, after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static EventStatus? ParseEventStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return null;
        try { return EventStatusMap.FromDbValue(status); }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidListFilterException("Event status must be accepted, processing, routed, unrouted, failed, or dead_lettered.");
        }
    }
}
