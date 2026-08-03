using System.Text.Json;
using Integrios.Application.Integrations;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class IntegrationsEndpoints : IEndpointGroup
{
    private const string GetByVersionRouteName = "GetIntegrationByVersion";

    public string Prefix => "/integrations";

    public void Map(RouteGroupBuilder group)
    {
        group.MapGet(ListIntegrations);
        group.MapGet(GetIntegrationById, "/{id:guid}");
        group.MapGet(GetIntegrationByVersion, "/{key}/versions/{contractVersion:int}")
            .WithName(GetByVersionRouteName);
        group.MapPut(ApplyIntegrationManifest, "/{key}/versions/{contractVersion:int}");
    }

    private static async Task<IResult> ListIntegrations(
        IMediator mediator,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
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

    private static async Task<IResult> GetIntegrationByVersion(
        string key,
        int contractVersion,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        IntegrationResponse? response = await mediator.Send(
            new GetIntegrationByVersionQuery(key, contractVersion),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> ApplyIntegrationManifest(
        string key,
        int contractVersion,
        JsonElement manifest,
        IMediator mediator,
        HttpContext httpContext,
        LinkGenerator links,
        CancellationToken cancellationToken)
    {
        ApplyIntegrationManifestResult result = await mediator.Send(
            new ApplyIntegrationManifestCommand(key, contractVersion, manifest),
            cancellationToken);

        if (result.Outcome != IntegrationManifestApplyOutcome.Created)
            return Results.Ok(result.Integration);

        string location = links.GetPathByName(
            httpContext,
            GetByVersionRouteName,
            new { key, contractVersion })
            ?? throw new InvalidOperationException("The Integration version route could not be generated.");
        return Results.Created(location, result.Integration);
    }
}
