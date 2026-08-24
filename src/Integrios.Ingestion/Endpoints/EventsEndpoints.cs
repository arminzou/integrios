using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Ingestion.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

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
        [FromQuery(Name = "source_id")] Guid sourceId,
        [FromBody] JsonElement request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var tenantContext = httpContext.GetTenantContext();
        var response = await mediator.Send(
            new IngestEventCommand(tenantContext.Tenant.Id, sourceId, request),
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
