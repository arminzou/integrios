using System.Text.Json;
using Integrios.Application.Authoring.Destinations;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class DestinationsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/destinations";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateDestination).Produces<DestinationDto>(StatusCodes.Status201Created);
        group.MapGet(ListDestinations).Produces<DestinationListDto>();
        group.MapGet(GetDestinationById, "/{id:guid}").Produces<DestinationDto>();
        group.MapPatch(UpdateDestination, "/{id:guid}").Produces<DestinationDto>();
        group.MapPost(DeactivateDestination, "/{id:guid}/deactivate");
    }

    private static async Task<IResult> CreateDestination(
        Guid tenantId,
        CreateDestinationRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        DestinationDto response = await mediator.Send(
            new CreateDestinationCommand(
                tenantId,
                request.ConnectorId,
                request.Name,
                request.Configuration,
                request.Authentication?.ToInput(),
                request.Environment,
                request.Description),
            cancellationToken);
        return Results.Created($"/admin/tenants/{tenantId}/destinations/{response.Id}", response);
    }

    private static async Task<IResult> ListDestinations(
        Guid tenantId,
        IMediator mediator,
        string? status,
        string? environment,
        string? connector,
        string? name,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var filter = new DestinationListFilter(
            ListFilter.ParseEnum<OperationalStatus>(status, "Destination status must be active or disabled."),
            ListFilter.Trimmed(environment),
            ListFilter.Trimmed(connector),
            ListFilter.Trimmed(name));
        DestinationListDto response = await mediator.Send(
            new ListDestinationsByTenantQuery(tenantId, filter, after, limit),
            cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetDestinationById(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        DestinationDto? response = await mediator.Send(
            new GetDestinationByIdQuery(tenantId, id),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateDestination(
        Guid tenantId,
        Guid id,
        UpdateDestinationRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        DestinationDto? response = await mediator.Send(
            new UpdateDestinationCommand(
                tenantId,
                id,
                request.Name,
                request.Configuration,
                request.Authentication?.ToInput(),
                request.Environment,
                request.Description),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateDestination(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        bool deactivated = await mediator.Send(new DeactivateDestinationCommand(tenantId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateDestinationRequest(
    Guid ConnectorId,
    string? Name,
    JsonElement Configuration,
    DestinationAuthenticationSelectionRequest? Authentication,
    string? Environment,
    string? Description);

internal sealed record UpdateDestinationRequest(
    string? Name,
    JsonElement Configuration,
    DestinationAuthenticationSelectionRequest? Authentication,
    string? Environment,
    string? Description);

internal sealed record DestinationAuthenticationSelectionRequest(
    string Scheme,
    JsonElement Config,
    JsonElement SecretRefs)
{
    public DestinationAuthenticationInput ToInput() => new()
    {
        Scheme = Scheme,
        Config = Config,
        SecretRefs = SecretRefs,
    };
}
