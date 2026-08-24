using System.Text.Json;
using Integrios.Application.Sources;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class SourcesEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/sources";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateSource);
        group.MapGet(ListSources);
        group.MapGet(GetSourceById, "/{id:guid}");
        group.MapPatch(UpdateSource, "/{id:guid}");
        group.MapDelete(RevokeSource, "/{id:guid}");
    }

    private static async Task<IResult> CreateSource(Guid tenantId, CreateSourceRequest request, IMediator mediator, CancellationToken cancellationToken)
    {
        SourceType type = request.Type switch
        {
            "event_api" => SourceType.EventApi,
            "webhook" => SourceType.Webhook,
            "queue" => SourceType.Queue,
            _ => throw new SourceValidationException("Source type must be event_api, webhook, or queue.")
        };
        SourceDto source = await mediator.Send(new CreateSourceCommand(tenantId, request.ConnectionId, request.TopicId, type, request.Configuration), cancellationToken);
        return Results.Created($"/admin/tenants/{tenantId}/sources/{source.Id}", source);
    }

    private static async Task<IResult> ListSources(Guid tenantId, IMediator mediator, string? after, int limit = 0, CancellationToken cancellationToken = default)
    {
        SourceListDto sources = await mediator.Send(new ListSourcesQuery(tenantId, after, Math.Clamp(limit == 0 ? 20 : limit, 1, 100)), cancellationToken);
        return Results.Ok(sources);
    }

    private static async Task<IResult> GetSourceById(Guid tenantId, Guid id, IMediator mediator, CancellationToken cancellationToken)
    {
        SourceDto? source = await mediator.Send(new GetSourceByIdQuery(tenantId, id), cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }

    private static async Task<IResult> UpdateSource(Guid tenantId, Guid id, UpdateSourceRequest request, IMediator mediator, CancellationToken cancellationToken)
    {
        SourceDto? source = await mediator.Send(new UpdateSourceCommand(tenantId, id, request.Configuration), cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }

    private static async Task<IResult> RevokeSource(Guid tenantId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        await mediator.Send(new RevokeSourceCommand(tenantId, id), cancellationToken) ? Results.Ok() : Results.NotFound();
}

internal sealed record CreateSourceRequest(Guid ConnectionId, Guid TopicId, string Type, JsonElement Configuration);
internal sealed record UpdateSourceRequest(JsonElement Configuration);
