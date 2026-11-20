using Integrios.Application.Authoring.SubscriberKeys;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class SubscriberKeysEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/destinations/{destinationId:guid}/subscriber-keys";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateSubscriberKey).Produces<CreateSubscriberKeyResult>(StatusCodes.Status201Created);
        group.MapGet(ListSubscriberKeys).Produces<SubscriberKeyListDto>();
        group.MapGet(GetSubscriberKeyById, "/{id:guid}").Produces<SubscriberKeyDto>();
        group.MapPost(RevokeSubscriberKey, "/{id:guid}/revoke");
        group.MapDelete(DeleteSubscriberKey, "/{id:guid}");
    }

    private static async Task<IResult> CreateSubscriberKey(
        Guid tenantId, Guid destinationId,
        CreateSubscriberKeyRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        CreateSubscriberKeyResult response = await mediator.Send(
            new CreateSubscriberKeyCommand(tenantId, destinationId, request.Name, request.Description),
            cancellationToken);
        return Results.Created($"/admin/tenants/{tenantId}/destinations/{destinationId}/subscriber-keys/{response.SubscriberKey.Id}", response);
    }

    private static async Task<IResult> ListSubscriberKeys(
        Guid tenantId, Guid destinationId,
        IMediator mediator,
        string? state,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        SubscriberKeyListDto response = await mediator.Send(
            new ListSubscriberKeysByDestinationQuery(tenantId, destinationId, ListFilter.ParseEnum<SubscriberKeyListState>(state, "Subscriber key state must be active or revoked."), after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetSubscriberKeyById(
        Guid tenantId, Guid destinationId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        SubscriberKeyDto? response = await mediator.Send(new GetSubscriberKeyByIdQuery(tenantId, destinationId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> RevokeSubscriberKey(
        Guid tenantId, Guid destinationId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        bool revoked = await mediator.Send(new RevokeSubscriberKeyCommand(tenantId, destinationId, id), cancellationToken);
        return revoked ? Results.Ok() : Results.NotFound();
    }

    private static async Task<IResult> DeleteSubscriberKey(
        Guid tenantId, Guid destinationId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        bool deleted = await mediator.Send(new DeleteSubscriberKeyCommand(tenantId, destinationId, id), cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}

internal sealed record CreateSubscriberKeyRequest(string? Name, string? Description);
