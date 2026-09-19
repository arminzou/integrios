using System.Text.Json.Serialization;
using Integrios.Application.Authoring.Topics;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class TopicsEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/topics";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateTopic).Produces<AdminTopicResponse>(StatusCodes.Status201Created);
        group.MapGet(ListTopics).Produces<AdminTopicListResponse>();
        group.MapGet(GetTopicById, "/{id:guid}").Produces<AdminTopicResponse>();
        group.MapPut(UpdateTopic, "/{id:guid}").Produces<AdminTopicResponse>();
        group.MapDelete(DeleteTopic, "/{id:guid}");
    }

    private static async Task<IResult> CreateTopic(
        Guid tenantId,
        CreateTopicRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(
            new CreateTopicCommand(tenantId, request.Key, request.Name, request.Description),
            cancellationToken);
        var response = AdminTopicResponse.From(dto);
        return Results.Created($"/admin/tenants/{tenantId}/topics/{response.Id}", response);
    }

    private static async Task<IResult> ListTopics(
        Guid tenantId,
        IMediator mediator,
        CancellationToken cancellationToken,
        string? name = null,
        string? after = null,
        int limit = 20)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var filter = new TopicListFilter(ListFilter.Trimmed(name));
        var dto = await mediator.Send(new ListTopicsByTenantQuery(tenantId, filter, after, limit), cancellationToken);
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

    private static async Task<IResult> DeleteTopic(
        Guid tenantId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        await mediator.Send(new DeleteTopicCommand(tenantId, id), cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
}

internal sealed record CreateTopicRequest(
    string? Key,
    string? Name,
    string? Description);

internal sealed record UpdateTopicRequest(
    [property: JsonRequired] string? Name,
    [property: JsonRequired] string? Description);

internal sealed record AdminTopicResponse(
    Guid Id,
    Guid TenantId,
    string Key,
    string Name,
    string? Description,
    int SubscriptionCount,
    IReadOnlyList<string> EventTypes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AdminTopicResponse From(TopicDto dto) => new(
        dto.Id,
        dto.TenantId,
        dto.Key,
        dto.Name,
        dto.Description,
        dto.SubscriptionCount,
        dto.EventTypes,
        dto.CreatedAt,
        dto.UpdatedAt);
}

internal sealed record AdminTopicListResponse(IReadOnlyList<AdminTopicResponse> Items, string? NextCursor)
{
    public static AdminTopicListResponse From(TopicListDto dto) => new(
        dto.Items.Select(AdminTopicResponse.From).ToList(),
        dto.NextCursor);
}
