using System.Text.Json;
using Integrios.Admin.Auth;
using Integrios.Application.Connections;
using Integrios.Application.Connections.Commands;
using Integrios.Application.Connections.Queries;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class ConnectionsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/connections";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateConnection);
        group.MapGet(ListConnections);
        group.MapGet(GetConnectionById, "/{id:guid}");
        group.MapPatch(UpdateConnection, "/{id:guid}");
        group.MapPost(DeactivateConnection, "/{id:guid}/deactivate");
    }

    private static async Task<IResult> CreateConnection(
        Guid tenantId,
        CreateConnectionRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        try
        {
            ConnectionResponse response = await mediator.Send(
                new CreateConnectionCommand(tenantId, request.IntegrationId, request.Name, request.Config, request.Environment, request.Description),
                cancellationToken);
            return Results.Created($"/admin/tenants/{tenantId}/connections/{response.Id}", response);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("integration does not exist"))
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ListConnections(
        Guid tenantId,
        HttpContext httpContext,
        IMediator mediator,
        string? after,
        int limit,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        ConnectionListResponse response = await mediator.Send(
            new ListConnectionsByTenantQuery(tenantId, after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetConnectionById(
        Guid tenantId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        ConnectionResponse? response = await mediator.Send(new GetConnectionByIdQuery(tenantId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateConnection(
        Guid tenantId,
        Guid id,
        UpdateConnectionRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        ConnectionResponse? response = await mediator.Send(
            new UpdateConnectionCommand(tenantId, id, request.Name, request.Config, request.Environment, request.Description),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateConnection(
        Guid tenantId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        AdminPrincipal principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        bool deactivated = await mediator.Send(new DeactivateConnectionCommand(tenantId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateConnectionRequest(Guid IntegrationId, string Name, JsonElement Config, string? Environment, string? Description);
internal sealed record UpdateConnectionRequest(string Name, JsonElement Config, string? Environment, string? Description);
