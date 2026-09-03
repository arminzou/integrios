using Integrios.Application.Delivery;
using Integrios.Application.Ingestion;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class DeliveryRecoveryEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/events/{eventId:guid}/deliveries";

    public void Map(RouteGroupBuilder group)
    {
        group.MapGet(GetDeliveries).Produces<EventDto>();
        group.MapPost(ReplayDelivery, "/{deliveryId:guid}/replay");
    }

    private static async Task<IResult> GetDeliveries(
        Guid tenantId,
        Guid eventId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        EventDto? response = await mediator.Send(
            new GetEventDeliveryRecoveryQuery(tenantId, eventId),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> ReplayDelivery(
        Guid tenantId,
        Guid eventId,
        Guid deliveryId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        DeadLetterReplayResult result = await mediator.Send(
            new ReplayEventDeliveryCommand(tenantId, eventId, deliveryId),
            cancellationToken);

        return result switch
        {
            DeadLetterReplayResult.Replayed => Results.Accepted(
                $"/admin/tenants/{tenantId}/events/{eventId}/deliveries"),
            DeadLetterReplayResult.NotDeadLettered => Results.Conflict(),
            _ => Results.NotFound()
        };
    }
}
