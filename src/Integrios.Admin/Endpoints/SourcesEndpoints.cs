using System.Text.Json;
using System.Text.Json.Serialization;
using Integrios.Application.Authoring.Sources;
using Integrios.Application.Common.Exceptions;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class SourcesEndpoints : IEndpointGroup
{
    public string Prefix => "/tenants/{tenantId:guid}/sources";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(CreateSource).Produces<SourceDto>(StatusCodes.Status201Created);
        group.MapGet(ListSources).Produces<SourceListDto>();
        group.MapGet(GetSourceById, "/{id:guid}").Produces<SourceDto>();
        group.MapPut(UpdateSource, "/{id:guid}").Produces<SourceDto>();
        group.MapPost(ActivateSource, "/{id:guid}/activate").Produces<SourceDto>();
        group.MapPost(DeactivateSource, "/{id:guid}/deactivate").Produces<SourceDto>();
        group.MapDelete(DeleteSource, "/{id:guid}");
    }

    private static async Task<IResult> CreateSource(Guid tenantId, CreateSourceRequest request, IMediator mediator, CancellationToken cancellationToken)
    {
        SourceType type = request.Type switch
        {
            "event_api" => SourceType.EventApi,
            "webhook" => SourceType.Webhook,
            "broker" => SourceType.Broker,
            _ => throw new SourceValidationException("Source type must be event_api, webhook, or broker.", "type")
        };
        SourceDto source = await mediator.Send(
            new CreateSourceCommand(
                tenantId,
                request.ConnectorId,
                request.TopicId,
                request.Name,
                type,
                request.Configuration,
                request.Verification?.ToInput(),
                request.InputRequirements,
                request.Mapping,
                request.EventIdentityRule,
                request.EventTypes),
            cancellationToken);
        return Results.Created($"/admin/tenants/{tenantId}/sources/{source.Id}", source);
    }

    private static async Task<IResult> ListSources(Guid tenantId, IMediator mediator, string? status, string? type, Guid? topic_id, string? after, int limit = 0, CancellationToken cancellationToken = default)
    {
        SourceType? sourceType = type switch
        {
            null or "" => null,
            "event_api" => SourceType.EventApi,
            "webhook" => SourceType.Webhook,
            "broker" => SourceType.Broker,
            _ => throw new InvalidListFilterException("Source type must be event_api, webhook, or broker."),
        };
        SourceListDto sources = await mediator.Send(new ListSourcesQuery(tenantId, ListFilter.ParseEnum<OperationalStatus>(status, "Source status must be active or inactive."), sourceType, topic_id, after, Math.Clamp(limit == 0 ? 20 : limit, 1, 100)), cancellationToken);
        return Results.Ok(sources);
    }

    private static async Task<IResult> GetSourceById(Guid tenantId, Guid id, IMediator mediator, CancellationToken cancellationToken)
    {
        SourceDto? source = await mediator.Send(new GetSourceByIdQuery(tenantId, id), cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }

    private static async Task<IResult> UpdateSource(Guid tenantId, Guid id, UpdateSourceRequest request, IMediator mediator, CancellationToken cancellationToken)
    {
        SourceDto? source = await mediator.Send(
            new UpdateSourceCommand(
                tenantId, id, request.Name, request.Configuration, request.Verification?.ToInput(), request.InputRequirements,
                request.Mapping, request.EventIdentityRule, request.EventTypes),
            cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }

    private static Task<IResult> ActivateSource(Guid tenantId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        SetStatus(tenantId, id, OperationalStatus.Active, mediator, cancellationToken);

    private static Task<IResult> DeactivateSource(Guid tenantId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        SetStatus(tenantId, id, OperationalStatus.Inactive, mediator, cancellationToken);

    private static async Task<IResult> SetStatus(
        Guid tenantId, Guid id, OperationalStatus status, IMediator mediator, CancellationToken cancellationToken)
    {
        SourceDto? source = await mediator.Send(new SetSourceStatusCommand(tenantId, id, status), cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }

    private static async Task<IResult> DeleteSource(
        Guid tenantId, Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        await mediator.Send(new DeleteSourceCommand(tenantId, id), cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
}

internal sealed record CreateSourceRequest(
    Guid ConnectorId,
    Guid TopicId,
    string? Name,
    string? Type,
    JsonElement Configuration,
    SourceVerificationSelectionRequest? Verification,
    JsonElement? InputRequirements,
    SourceMapping? Mapping,
    SourceEventIdentityRule? EventIdentityRule,
    IReadOnlyList<string>? EventTypes);
internal sealed record UpdateSourceRequest(
    [property: JsonRequired] string? Name,
    [property: JsonRequired] JsonElement Configuration,
    [property: JsonRequired] SourceVerificationSelectionRequest? Verification,
    [property: JsonRequired] JsonElement? InputRequirements,
    [property: JsonRequired] SourceMapping? Mapping,
    [property: JsonRequired] SourceEventIdentityRule? EventIdentityRule,
    [property: JsonRequired] IReadOnlyList<string>? EventTypes);

internal sealed record SourceVerificationSelectionRequest(string Scheme, JsonElement Config, JsonElement SecretRefs)
{
    public SourceVerificationInput ToInput() => new()
    {
        Scheme = Scheme,
        Config = Config,
        SecretRefs = SecretRefs,
    };
}
