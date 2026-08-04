using Integrios.Application.Topics;
using MediatR;
using System.Text.Json.Serialization;

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
        PublicIngressUri publicIngressUri,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new CreateTopicCommand(tenantId, request.Name, request.Description, request.SourceConnectionIds ?? []),
            cancellationToken);
        return Results.Created(
            $"/admin/tenants/{tenantId}/topics/{response.Id}",
            AdminTopicResponse.From(response, publicIngressUri));
    }

    private static async Task<IResult> ListTopics(
        Guid tenantId,
        IMediator mediator,
        PublicIngressUri publicIngressUri,
        CancellationToken cancellationToken,
        string? after = null,
        int limit = 20)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        var response = await mediator.Send(new ListTopicsByTenantQuery(tenantId, after, limit), cancellationToken);
        return Results.Ok(new AdminTopicListResponse(
            response.Items.Select(item => AdminTopicResponse.From(item, publicIngressUri)).ToList(),
            response.NextCursor));
    }

    private static async Task<IResult> GetTopicById(
        Guid tenantId,
        Guid id,
        IMediator mediator,
        PublicIngressUri publicIngressUri,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new GetTopicByIdQuery(tenantId, id), cancellationToken);
        return response is null
            ? Results.NotFound()
            : Results.Ok(AdminTopicResponse.From(response, publicIngressUri));
    }

    private static async Task<IResult> UpdateTopic(
        Guid tenantId,
        Guid id,
        UpdateTopicRequest request,
        IMediator mediator,
        PublicIngressUri publicIngressUri,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(
            new UpdateTopicCommand(tenantId, id, request.Name, request.Description, request.SourceConnectionIds),
            cancellationToken);
        return response is null
            ? Results.NotFound()
            : Results.Ok(AdminTopicResponse.From(response, publicIngressUri));
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
    IReadOnlyList<Guid> SourceConnectionIds,
    IReadOnlyList<AdminSourceEndpointResponse> SourceEndpoints,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AdminTopicResponse From(TopicResponse topic, PublicIngressUri publicIngressUri) => new(
        topic.Id,
        topic.TenantId,
        topic.Name,
        topic.SourceConnectionIds,
        topic.SourceEndpoints.Select(endpoint => AdminSourceEndpointResponse.From(endpoint, publicIngressUri)).ToList(),
        topic.Status,
        topic.Description,
        topic.CreatedAt,
        topic.UpdatedAt);
}

internal sealed record AdminSourceEndpointResponse(
    Guid Id,
    Guid SourceConnectionId,
    [property: JsonPropertyName("callback_path")] string CallbackPath,
    [property: JsonPropertyName("callback_url")] string CallbackUrl)
{
    public static AdminSourceEndpointResponse From(
        SourceEndpointResponse endpoint,
        PublicIngressUri publicIngressUri) => new(
        endpoint.Id,
        endpoint.SourceConnectionId,
        endpoint.CallbackPath,
        publicIngressUri.AppendCallbackPath(endpoint.CallbackPath));
}

internal sealed record AdminTopicListResponse(
    IReadOnlyList<AdminTopicResponse> Items,
    string? NextCursor);
