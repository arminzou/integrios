using Integrios.Application.Authoring.Subscriptions;
using Integrios.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Integrios.Admin.Endpoints;

public sealed class SubscriptionsByTenantEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/subscriptions";

    public void Map(RouteGroupBuilder group)
    {
        group.MapGet(ListSubscriptionsByTenant).Produces<SubscriptionByTenantListDto>();
    }

    private static async Task<IResult> ListSubscriptionsByTenant(
        Guid tenantId,
        IMediator mediator,
        string? status,
        [FromQuery(Name = "topic_id")] Guid? topicId,
        [FromQuery(Name = "connection_id")] Guid? connectionId,
        string? name,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var filter = new SubscriptionListFilter(
            ListFilter.ParseEnum<OperationalStatus>(status, "Subscription status must be active or disabled."),
            topicId,
            connectionId,
            ListFilter.Trimmed(name));
        SubscriptionByTenantListDto response = await mediator.Send(
            new ListSubscriptionsByTenantQuery(tenantId, filter, after, limit),
            cancellationToken);
        return Results.Ok(response);
    }
}
