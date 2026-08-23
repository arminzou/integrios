using System.Text.Json;
using Integrios.Application.Connections;
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
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ConnectionDto response = await mediator.Send(
            new CreateConnectionCommand(
                tenantId,
                request.ConnectorId,
                request.Name,
                request.Config,
                request.SourceVerification?.ToInput(),
                request.DestinationAuthentication?.ToInput(),
                request.Environment,
                request.Description),
            cancellationToken);

        return Results.Created($"/admin/tenants/{tenantId}/connections/{response.Id}", response);
    }

    private static async Task<IResult> ListConnections(
        Guid tenantId,
        IMediator mediator,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        ConnectionListDto response = await mediator.Send(
            new ListConnectionsByTenantQuery(tenantId, after, limit),
            cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetConnectionById(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ConnectionDto? response = await mediator.Send(new GetConnectionByIdQuery(tenantId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateConnection(
        Guid tenantId,
        Guid id,
        UpdateConnectionRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ConnectionDto? response = await mediator.Send(
            new UpdateConnectionCommand(
                tenantId,
                id,
                request.Name,
                request.Config,
                request.SourceVerification?.ToInput(),
                request.DestinationAuthentication?.ToInput(),
                request.Environment,
                request.Description),
            cancellationToken);

        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateConnection(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        bool deactivated = await mediator.Send(new DeactivateConnectionCommand(tenantId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateConnectionRequest(
    Guid ConnectorId,
    string Name,
    JsonElement Config,
    ConnectionSchemeSelectionRequest? SourceVerification,
    ConnectionSchemeSelectionRequest? DestinationAuthentication,
    string? Environment,
    string? Description);

internal sealed record UpdateConnectionRequest(
    string Name,
    JsonElement Config,
    ConnectionSchemeSelectionRequest? SourceVerification,
    ConnectionSchemeSelectionRequest? DestinationAuthentication,
    string? Environment,
    string? Description);

internal sealed record ConnectionSchemeSelectionRequest(
    string Scheme,
    JsonElement Config,
    JsonElement SecretRefs)
{
    public ConnectionSchemeSelectionInput ToInput() => new()
    {
        Scheme = Scheme,
        Config = Config,
        SecretRefs = SecretRefs
    };
}
