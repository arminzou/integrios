using System.Text.Json;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class SubscriptionsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/topics/{topicId:guid}/subscriptions";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateSubscription).Produces<SubscriptionDto>(StatusCodes.Status201Created);
        group.MapGet(ListSubscriptions).Produces<SubscriptionListDto>();
        group.MapGet(GetSubscriptionById, "/{id:guid}").Produces<SubscriptionDto>();
        group.MapPatch(UpdateSubscription, "/{id:guid}").Produces<SubscriptionDto>();
        group.MapPost(DeactivateSubscription, "/{id:guid}/deactivate");
    }

    private static async Task<IResult> CreateSubscription(
        Guid tenantId,
        Guid topicId,
        CreateSubscriptionRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateSubscriptionCommand(
                tenantId,
                topicId,
                request.Name,
                request.MatchRules,
                request.DestinationConnectionId,
                request.Mapping,
                request.HttpDelivery ?? HttpDeliveryConfiguration.Default,
                request.OrderIndex,
                request.Description),
            cancellationToken);

        return response is null
            ? Results.NotFound()
            : Results.Created($"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{response.Id}", response);
    }

    private static async Task<IResult> ListSubscriptions(
        Guid tenantId,
        Guid topicId,
        IMediator mediator,
        CancellationToken cancellationToken,
        string? status,
        string? after,
        int limit = 0)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var response = await mediator.Send(new ListSubscriptionsByTopicQuery(tenantId, topicId, ListFilter.ParseEnum<OperationalStatus>(status, "Subscription status must be active or disabled."), after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetSubscriptionById(
        Guid tenantId,
        Guid topicId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new GetSubscriptionByIdQuery(tenantId, topicId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateSubscription(
        Guid tenantId,
        Guid topicId,
        Guid id,
        UpdateSubscriptionRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new UpdateSubscriptionCommand(
                tenantId,
                topicId,
                id,
                request.Name,
                request.MatchRules,
                request.DestinationConnectionId,
                request.Mapping,
                request.HttpDelivery ?? HttpDeliveryConfiguration.Default,
                request.OrderIndex,
                request.Description),
            cancellationToken);

        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateSubscription(
        Guid tenantId,
        Guid topicId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        bool deactivated = await mediator.Send(new DeactivateSubscriptionCommand(tenantId, topicId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }

}

internal sealed record CreateSubscriptionRequest(
    string? Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? Mapping,
    HttpDeliveryConfiguration? HttpDelivery,
    int OrderIndex,
    string? Description);

internal sealed record UpdateSubscriptionRequest(
    string? Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? Mapping,
    HttpDeliveryConfiguration? HttpDelivery,
    int OrderIndex,
    string? Description);
