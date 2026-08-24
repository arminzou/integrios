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
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(
            new CreateTopicCommand(tenantId, request.Name, request.Description),
            cancellationToken);
        var response = AdminTopicResponse.From(dto);
        return Results.Created($"/admin/tenants/{tenantId}/topics/{response.Id}", response);
    }

    private static async Task<IResult> ListTopics(
        Guid tenantId,
        IMediator mediator,
        CancellationToken cancellationToken,
        string? after = null,
        int limit = 20)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var dto = await mediator.Send(new ListTopicsByTenantQuery(tenantId, after, limit), cancellationToken);
        return Results.Ok(AdminTopicListResponse.From(dto));
    }

    private static async Task<IResult> GetTopicById(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(new GetTopicByIdQuery(tenantId, id), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(AdminTopicResponse.From(dto));
    }

    private static async Task<IResult> UpdateTopic(
        Guid tenantId,
        Guid id,
        UpdateTopicRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(
            new UpdateTopicCommand(tenantId, id, request.Name, request.Description),
            cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(AdminTopicResponse.From(dto));
    }

    private static async Task<IResult> DeactivateTopic(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var deactivated = await mediator.Send(new DeactivateTopicCommand(tenantId, id), cancellationToken);
        return deactivated ? Results.Ok() : Results.NotFound();
    }
}

internal sealed record CreateTopicRequest(
    string Name,
    string? Description);

internal sealed record UpdateTopicRequest(
    string? Name,
    string? Description);

internal sealed record AdminTopicResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AdminTopicResponse From(TopicDto dto) => new(
        dto.Id,
        dto.TenantId,
        dto.Name,
        dto.Status,
        dto.Description,
        dto.CreatedAt,
        dto.UpdatedAt);
}

internal sealed record AdminTopicListResponse(IReadOnlyList<AdminTopicResponse> Items, string? NextCursor)
{
    public static AdminTopicListResponse From(TopicListDto dto) => new(
        dto.Items.Select(AdminTopicResponse.From).ToList(),
        dto.NextCursor);
}
