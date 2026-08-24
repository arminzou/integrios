using Integrios.Application.Ingestion;
using Integrios.Ingestion.Auth;
using MediatR;

namespace Integrios.Ingestion.Endpoints;

public sealed class EventsEndpoints : IEndpointGroup
{
    public string Prefix => "/events";

    public void Map(RouteGroupBuilder group)
    {
        group.RequireAuthorization();
        group.MapPost(IngestEvent);
        group.MapGet(GetEventById, "/{id:guid}");
    }

    private static async Task<IResult> IngestEvent(
        IngestEventRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var tenantContext = httpContext.GetTenantContext();
        var response = await mediator.Send(
            new IngestEventCommand(
                tenantContext.Tenant.Id,
                request.SourceId,
                request.TopicName,
                request.SourceEventId,
                request.EventType,
                request.Payload,
                request.Metadata,
                request.IdempotencyKey),
            cancellationToken);
        return Results.Accepted($"/events/{response.EventId}", response);
    }

    private static async Task<IResult> GetEventById(
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var tenantContext = httpContext.GetTenantContext();
        var response = await mediator.Send(
            new GetEventByIdQuery(tenantContext.Tenant.Id, id),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

}
