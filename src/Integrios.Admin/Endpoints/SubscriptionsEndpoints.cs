using System.Text.Json;
using Integrios.Admin.Auth;
using Integrios.Application.Subscriptions;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class SubscriptionsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/topics/{topicId:guid}/subscriptions";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateSubscription);
        group.MapGet(ListSubscriptions);
        group.MapGet(GetSubscriptionById, "/{id:guid}");
        group.MapPatch(UpdateSubscription, "/{id:guid}");
        group.MapPost(DeactivateSubscription, "/{id:guid}/deactivate");
    }

    private static async Task<IResult> CreateSubscription(
        Guid tenantId,
        Guid topicId,
        CreateSubscriptionRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var response = await mediator.Send(
            new CreateSubscriptionCommand(
                tenantId,
                topicId,
                request.Name,
                request.MatchRules,
                request.DestinationConnectionId,
                request.DlqEnabled,
                request.OrderIndex,
                request.Description),
            cancellationToken);

        return Results.Created($"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{response.Id}", response);
    }

    private static async Task<IResult> ListSubscriptions(
        Guid tenantId,
        Guid topicId,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken,
        string? after = null,
        int limit = 20)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        limit = Math.Clamp(limit, 1, 100);
        var response = await mediator.Send(new ListSubscriptionsByTopicQuery(tenantId, topicId, after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetSubscriptionById(
        Guid tenantId,
        Guid topicId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var response = await mediator.Send(new GetSubscriptionByIdQuery(tenantId, topicId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateSubscription(
        Guid tenantId,
        Guid topicId,
        Guid id,
        UpdateSubscriptionRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var response = await mediator.Send(
            new UpdateSubscriptionCommand(
                tenantId,
                topicId,
                id,
                request.Name,
                request.MatchRules,
                request.DestinationConnectionId,
                request.DlqEnabled,
                request.OrderIndex,
                request.Description),
            cancellationToken);

        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateSubscription(
        Guid tenantId,
        Guid topicId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var deactivated = await mediator.Send(new DeactivateSubscriptionCommand(tenantId, topicId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateSubscriptionRequest(
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    bool DlqEnabled,
    int OrderIndex,
    string? Description);

internal sealed record UpdateSubscriptionRequest(
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    bool DlqEnabled,
    int OrderIndex,
    string? Description);
