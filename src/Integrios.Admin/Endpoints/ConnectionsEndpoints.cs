using System.Text.Json;
using System.Text.Json.Serialization;
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
        ConnectionResponse response = await mediator.Send(
            new CreateConnectionCommand(
                tenantId,
                request.IntegrationId,
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
        ConnectionListResponse response = await mediator.Send(
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
        ConnectionResponse? response = await mediator.Send(new GetConnectionByIdQuery(tenantId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateConnection(
        Guid tenantId,
        Guid id,
        UpdateConnectionRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ConnectionResponse? response = await mediator.Send(
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
    Guid IntegrationId,
    string Name,
    JsonElement Config,
    [property: JsonPropertyName("source_verification")] ConnectionSchemeSelectionRequest? SourceVerification,
    [property: JsonPropertyName("destination_authentication")] ConnectionSchemeSelectionRequest? DestinationAuthentication,
    string? Environment,
    string? Description);

internal sealed record UpdateConnectionRequest(
    string Name,
    JsonElement Config,
    [property: JsonPropertyName("source_verification")] ConnectionSchemeSelectionRequest? SourceVerification,
    [property: JsonPropertyName("destination_authentication")] ConnectionSchemeSelectionRequest? DestinationAuthentication,
    string? Environment,
    string? Description);

internal sealed record ConnectionSchemeSelectionRequest(
    string Scheme,
    JsonElement Config,
    [property: JsonPropertyName("secret_refs")] JsonElement SecretRefs)
{
    public ConnectionSchemeSelectionInput ToInput() => new()
    {
        Scheme = Scheme,
        Config = Config,
        SecretRefs = SecretRefs
    };
}
