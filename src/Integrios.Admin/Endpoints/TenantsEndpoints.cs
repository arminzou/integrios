using Integrios.Admin.Auth;
using Integrios.Application.Tenants;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class TenantsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateTenant);
        group.MapGet(ListTenants);
        group.MapGet(GetTenantById, "/{id:guid}");
        group.MapPatch(UpdateTenant, "/{id:guid}");
        group.MapPost(DeactivateTenant, "/{id:guid}/deactivate");
    }

    private static async Task<IResult> CreateTenant(
        CreateTenantRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (!httpContext.GetAdminPrincipal().IsGlobal)
            return Results.Forbid();

        try
        {
            var response = await mediator.Send(
                new CreateTenantCommand(request.Slug, request.Name, request.Environment, request.Description),
                cancellationToken);
            return Results.Created($"/admin/tenants/{response.Id}", response);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ListTenants(
        HttpContext httpContext,
        IMediator mediator,
        string? after,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!httpContext.GetAdminPrincipal().IsGlobal)
            return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var response = await mediator.Send(new ListTenantsQuery(after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetTenantById(
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (!httpContext.GetAdminPrincipal().IsGlobal)
            return Results.Forbid();

        var response = await mediator.Send(new GetTenantByIdQuery(id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateTenant(
        Guid id,
        UpdateTenantRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (!httpContext.GetAdminPrincipal().IsGlobal)
            return Results.Forbid();

        var response = await mediator.Send(
            new UpdateTenantCommand(id, request.Name, request.Description, request.Environment),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateTenant(
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (!httpContext.GetAdminPrincipal().IsGlobal)
            return Results.Forbid();

        bool deactivated = await mediator.Send(new DeactivateTenantCommand(id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateTenantRequest(string Slug, string Name, string? Environment, string? Description);
internal sealed record UpdateTenantRequest(string Name, string? Description, string? Environment);
