using System.Text.Json;
using System.Text.Json.Serialization;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
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
        group.MapPut(UpdateSubscription, "/{id:guid}").Produces<SubscriptionDto>();
        group.MapPost(EnableSubscription, "/{id:guid}/enable").Produces<SubscriptionDto>();
        group.MapPost(DisableSubscription, "/{id:guid}/disable").Produces<SubscriptionDto>();
        group.MapDelete(DeleteSubscription, "/{id:guid}");
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
                request.EventTypes,
                request.DestinationId,
                request.Mapping,
                request.HttpDelivery ?? HttpDeliveryConfiguration.Default,
                request.HttpSuccess,
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
        var response = await mediator.Send(new ListSubscriptionsByTopicQuery(tenantId, topicId, ListFilter.ParseEnum<EnablementStatus>(status, "Subscription status must be enabled or disabled."), after, limit), cancellationToken);
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
                request.EventTypes,
                request.DestinationId,
                request.Mapping,
                request.HttpDelivery ?? HttpDeliveryConfiguration.Default,
                request.HttpSuccess,
                request.OrderIndex,
                request.Description),
            cancellationToken);

        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static Task<IResult> EnableSubscription(
        Guid tenantId, Guid topicId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        SetStatus(tenantId, topicId, id, EnablementStatus.Enabled, mediator, cancellationToken);

    private static Task<IResult> DisableSubscription(
        Guid tenantId, Guid topicId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        SetStatus(tenantId, topicId, id, EnablementStatus.Disabled, mediator, cancellationToken);

    private static async Task<IResult> SetStatus(
        Guid tenantId, Guid topicId, Guid id, EnablementStatus status, IMediator mediator, CancellationToken cancellationToken)
    {
        SubscriptionDto? subscription = await mediator.Send(
            new SetSubscriptionStatusCommand(tenantId, topicId, id, status), cancellationToken);
        return subscription is null ? Results.NotFound() : Results.Ok(subscription);
    }

    private static async Task<IResult> DeleteSubscription(
        Guid tenantId, Guid topicId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        await mediator.Send(new DeleteSubscriptionCommand(tenantId, topicId, id), cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

}

internal sealed record CreateSubscriptionRequest(
    string? Name,
    IReadOnlyList<string>? EventTypes,
    Guid DestinationId,
    JsonElement? Mapping,
    HttpDeliveryConfiguration? HttpDelivery,
    HttpSuccessRule? HttpSuccess,
    int OrderIndex,
    string? Description);

internal sealed record UpdateSubscriptionRequest(
    [property: JsonRequired] string? Name,
    [property: JsonRequired] IReadOnlyList<string>? EventTypes,
    [property: JsonRequired] Guid DestinationId,
    [property: JsonRequired] JsonElement? Mapping,
    [property: JsonRequired] HttpDeliveryConfiguration? HttpDelivery,
    [property: JsonRequired] HttpSuccessRule? HttpSuccess,
    [property: JsonRequired] int OrderIndex,
    [property: JsonRequired] string? Description);
