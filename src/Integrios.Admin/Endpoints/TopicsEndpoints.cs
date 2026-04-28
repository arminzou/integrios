using Integrios.Admin.Auth;
using Integrios.Application.Topics;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class TopicsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/topics";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateTopic);
        group.MapGet(ListTopics);
        group.MapGet(GetTopicById, "/{id:guid}");
        group.MapPatch(UpdateTopic, "/{id:guid}");
        group.MapPost(DeactivateTopic, "/{id:guid}/deactivate");
    }

    private static async Task<IResult> CreateTopic(
        Guid tenantId,
        CreateTopicRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var response = await mediator.Send(
            new CreateTopicCommand(tenantId, request.Name, request.Description, request.SourceConnectionIds ?? []),
            cancellationToken);
        return Results.Created($"/admin/tenants/{tenantId}/topics/{response.Id}", response);
    }

    private static async Task<IResult> ListTopics(
        Guid tenantId,
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
        var response = await mediator.Send(new ListTopicsByTenantQuery(tenantId, after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetTopicById(
        Guid tenantId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var response = await mediator.Send(new GetTopicByIdQuery(tenantId, id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> UpdateTopic(
        Guid tenantId,
        Guid id,
        UpdateTopicRequest request,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var response = await mediator.Send(
            new UpdateTopicCommand(tenantId, id, request.Name, request.Description, request.SourceConnectionIds),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> DeactivateTopic(
        Guid tenantId,
        Guid id,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = httpContext.GetAdminPrincipal();
        if (!principal.IsGlobal && principal.TenantId != tenantId)
            return Results.Forbid();

        var deactivated = await mediator.Send(new DeactivateTopicCommand(tenantId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateTopicRequest(
    string Name,
    string? Description,
    IReadOnlyList<Guid>? SourceConnectionIds);

internal sealed record UpdateTopicRequest(
    string Name,
    string? Description,
    IReadOnlyList<Guid>? SourceConnectionIds);
