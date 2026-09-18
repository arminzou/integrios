using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Unicode;
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
        // The JSON parser does not validate the UTF-8 inside strings, so a body carrying, say, a
        // Windows-1252 em dash binds here and faults later when a string is read. Refused now, the
        // way the framework refuses a body that is not JSON at all.
        if (!Utf8.IsValid(JsonMarshal.GetRawUtf8Value(request)))
            throw new BadHttpRequestException("The request body is not valid UTF-8.");

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
