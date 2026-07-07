using System.Text.Json;
using Integrios.Admin.Auth;
using Integrios.Application.Abstractions;
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
        ITransformEvaluator transformEvaluator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
        {
            return Results.Forbid();
        }

        var validationError = ValidateMatchRules(request.MatchRules) ?? ValidateTransformConfig(request.Transform, transformEvaluator);
        if (validationError is not null)
        {
            return validationError;
        }

        try
        {
            var response = await mediator.Send(
                new CreateSubscriptionCommand(
                    tenantId,
                    topicId,
                    request.Name,
                    request.MatchRules,
                    request.DestinationConnectionId,
                    request.Transform,
                    request.DlqEnabled,
                    request.OrderIndex,
                    request.Description),
                cancellationToken);

            return response is null
                ? Results.NotFound()
                : Results.Created($"/admin/tenants/{tenantId}/topics/{topicId}/subscriptions/{response.Id}", response);
        }
        catch (SubscriptionRequestValidationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ListSubscriptions(
        Guid tenantId,
        Guid topicId,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken,
        string? after,
        int limit = 0)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
        {
            return Results.Forbid();
        }

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
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
        {
            return Results.Forbid();
        }

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
        ITransformEvaluator transformEvaluator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
        {
            return Results.Forbid();
        }

        var validationError = ValidateMatchRules(request.MatchRules) ?? ValidateTransformConfig(request.Transform, transformEvaluator);
        if (validationError is not null)
        {
            return validationError;
        }

        try
        {
            var response = await mediator.Send(
                new UpdateSubscriptionCommand(
                    tenantId,
                    topicId,
                    id,
                    request.Name,
                    request.MatchRules,
                    request.DestinationConnectionId,
                    request.Transform,
                    request.DlqEnabled,
                    request.OrderIndex,
                    request.Description),
                cancellationToken);

            return response is null ? Results.NotFound() : Results.Ok(response);
        }
        catch (SubscriptionRequestValidationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }
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
        {
            return Results.Forbid();
        }

        bool deactivated = await mediator.Send(new DeactivateSubscriptionCommand(tenantId, topicId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }

    private static IResult? ValidateMatchRules(JsonElement matchRules)
    {
        if (matchRules.ValueKind != JsonValueKind.Object)
        {
            return InvalidMatchRules();
        }

        var enumerator = matchRules.EnumerateObject();
        if (!enumerator.MoveNext())
        {
            return InvalidMatchRules();
        }

        var property = enumerator.Current;
        if (property.Name != "event_type")
        {
            return InvalidMatchRules();
        }

        if (enumerator.MoveNext())
        {
            return InvalidMatchRules();
        }

        if (property.Value.ValueKind != JsonValueKind.String)
        {
            return InvalidMatchRules();
        }

        return string.IsNullOrWhiteSpace(property.Value.GetString()) ? InvalidMatchRules() : null;
    }

    private static IResult? ValidateTransformConfig(JsonElement? transform, ITransformEvaluator evaluator)
    {
        if (transform is null || transform.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var error = TransformConfig.Parse(transform.Value, evaluator, out _);
        return error is not null ? InvalidTransform(error) : null;
    }

    private static IResult InvalidMatchRules() =>
        Results.BadRequest(new { error = "matchRules must be an object with exactly one non-empty string property: event_type" });

    private static IResult InvalidTransform(string reason) =>
        Results.BadRequest(new { error = reason });
}

internal sealed record CreateSubscriptionRequest(
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? Transform,
    bool DlqEnabled,
    int OrderIndex,
    string? Description);

internal sealed record UpdateSubscriptionRequest(
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? Transform,
    bool DlqEnabled,
    int OrderIndex,
    string? Description);
