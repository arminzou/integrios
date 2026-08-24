using Integrios.Application.Authoring.Tenants;
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
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateTenantCommand(request.Slug, request.Name, request.Environment, request.Description),
            cancellationToken);
        return Results.Created($"/admin/tenants/{response.Id}", response);
    }

    private static async Task<IResult> ListTenants(
        IMediator mediator,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var response = await mediator.Send(new ListTenantsQuery(after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetTenantById(
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new GetTenantByIdQuery(id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateTenant(
        Guid id,
        UpdateTenantRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new UpdateTenantCommand(id, request.Name, request.Description, request.Environment),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateTenant(
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        bool deactivated = await mediator.Send(new DeactivateTenantCommand(id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateTenantRequest(string Slug, string Name, string? Environment, string? Description);
internal sealed record UpdateTenantRequest(string Name, string? Description, string? Environment);
