using Integrios.Application.Integrations;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class IntegrationsEndpoints : IEndpointGroup
{
    public string Prefix => "/integrations";

    public void Map(RouteGroupBuilder group)
    {
        group.MapGet(ListIntegrations);
        group.MapGet(GetIntegrationById, "/{id:guid}");
    }

    private static async Task<IResult> ListIntegrations(
        IMediator mediator,
        string? after,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        IntegrationListResponse response = await mediator.Send(new ListIntegrationsQuery(after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetIntegrationById(
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        IntegrationResponse? response = await mediator.Send(new GetIntegrationByIdQuery(id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }
}
