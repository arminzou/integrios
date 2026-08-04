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
        PublicIngressBaseUri publicIngressUri,
        CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(
            new CreateTopicCommand(tenantId, request.Name, request.Description, request.SourceConnectionIds ?? []),
            cancellationToken);
        var response = AdminTopicResponse.From(dto, publicIngressUri);
        return Results.Created($"/admin/tenants/{tenantId}/topics/{response.Id}", response);
    }

    private static async Task<IResult> ListTopics(
        Guid tenantId,
        IMediator mediator,
        PublicIngressBaseUri publicIngressUri,
        CancellationToken cancellationToken,
        string? after = null,
        int limit = 20)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var dto = await mediator.Send(new ListTopicsByTenantQuery(tenantId, after, limit), cancellationToken);
        return Results.Ok(AdminTopicListResponse.From(dto, publicIngressUri));
    }

    private static async Task<IResult> GetTopicById(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        PublicIngressBaseUri publicIngressUri,
        CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(new GetTopicByIdQuery(tenantId, id), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(AdminTopicResponse.From(dto, publicIngressUri));
    }

    private static async Task<IResult> UpdateTopic(
        Guid tenantId,
        Guid id,
        UpdateTopicRequest request,
        IMediator mediator,
        PublicIngressBaseUri publicIngressUri,
        CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(
            new UpdateTopicCommand(tenantId, id, request.Name, request.Description, request.SourceConnectionIds),
            cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(AdminTopicResponse.From(dto, publicIngressUri));
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
    string? Description,
    IReadOnlyList<Guid>? SourceConnectionIds);

internal sealed record UpdateTopicRequest(
    string? Name,
    string? Description,
    IReadOnlyList<Guid>? SourceConnectionIds);

internal sealed record AdminTopicResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    IReadOnlyList<AdminTopicSourceResponse> Sources,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AdminTopicResponse From(TopicDto dto, PublicIngressBaseUri publicIngressUri) => new(
        dto.Id,
        dto.TenantId,
        dto.Name,
        dto.Sources.Select(s => AdminTopicSourceResponse.From(s, publicIngressUri)).ToList(),
        dto.Status,
        dto.Description,
        dto.CreatedAt,
        dto.UpdatedAt);
}

internal sealed record AdminTopicSourceResponse(
    Guid ConnectionId,
    DateTimeOffset CreatedAt,
    AdminSourceEndpointResponse? Endpoint)
{
    public static AdminTopicSourceResponse From(TopicSourceDto dto, PublicIngressBaseUri publicIngressUri) => new(
        dto.ConnectionId,
        dto.CreatedAt,
        dto.Endpoint is null ? null : AdminSourceEndpointResponse.From(dto.Endpoint, publicIngressUri));
}

internal sealed record AdminSourceEndpointResponse(
    Guid Id,
    string CallbackPath,
    string CallbackUrl,
    DateTimeOffset CreatedAt)
{
    public static AdminSourceEndpointResponse From(SourceEndpointDto dto, PublicIngressBaseUri publicIngressUri) => new(
        dto.Id,
        dto.CallbackPath,
        publicIngressUri.AppendCallbackPath(dto.CallbackPath),
        dto.CreatedAt);
}

internal sealed record AdminTopicListResponse(IReadOnlyList<AdminTopicResponse> Items, string? NextCursor)
{
    public static AdminTopicListResponse From(TopicListDto dto, PublicIngressBaseUri publicIngressUri) => new(
        dto.Items.Select(t => AdminTopicResponse.From(t, publicIngressUri)).ToList(),
        dto.NextCursor);
}
