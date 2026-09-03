using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class ConnectorsEndpoints : IEndpointGroup
{
    private const string GetByVersionRouteName = "GetConnectorByVersion";

    public string Prefix => "/connectors";

    public void Map(RouteGroupBuilder group)
    {
        group.MapGet(ListConnectors).Produces<ConnectorListDto>();
        group.MapGet(GetConnectorById, "/{id:guid}").Produces<ConnectorDto>();
        group.MapGet(GetConnectorByVersion, "/{key}/versions/{contractVersion:int}")
            .WithName(GetByVersionRouteName)
            .Produces<ConnectorDto>();
        group.MapPut(ApplyConnectorManifest, "/{key}/versions/{contractVersion:int}")
            .Produces<ConnectorDto>()
            .Produces<ConnectorDto>(StatusCodes.Status201Created);
        group.MapPost(PreviewSourceContract, "/source-contracts/preview").Produces<PreviewResponse>();
    }

    private static async Task<IResult> ListConnectors(
        IMediator mediator,
        string? direction,
        string? after,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        ConnectorListDto response = await mediator.Send(new ListConnectorsQuery(ListFilter.ParseEnum<ConnectorDirection>(direction, "Connector direction must be source, destination, or both."), after, limit), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetConnectorById(
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ConnectorDto? response = await mediator.Send(new GetConnectorByIdQuery(id), cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> GetConnectorByVersion(
        string key,
        int contractVersion,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ConnectorDto? response = await mediator.Send(
            new GetConnectorByVersionQuery(key, contractVersion),
            cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static async Task<IResult> ApplyConnectorManifest(
        string key,
        int contractVersion,
        JsonElement manifest,
        IMediator mediator,
        HttpContext httpContext,
        LinkGenerator links,
        CancellationToken cancellationToken)
    {
        ApplyConnectorManifestResult result = await mediator.Send(
            new ApplyConnectorManifestCommand(key, contractVersion, manifest),
            cancellationToken);

        if (result.Outcome != ConnectorManifestApplyOutcome.Created)
            return Results.Ok(result.Connector);

        string location = links.GetPathByName(
            httpContext,
            GetByVersionRouteName,
            new { key, contractVersion })
            ?? throw new InvalidOperationException("The Connector version route could not be generated.");
        return Results.Created(location, result.Connector);
    }

    // Stateless dry-run: exercises the complete Source-contract pipeline (schema validation, JSONata
    // mapping, restricted output shape) against a sample input so an author can see the result
    // before saving. No tenant data is read, so any authenticated admin may call it.
    private static async Task<IResult> PreviewSourceContract(
        SourceContractPreviewRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        PreviewSourceContractResult result = await mediator.Send(
            new PreviewSourceContractQuery(request.Schema, request.Mapping, request.SampleInput, request.SampleContext),
            cancellationToken);
        if (result.Error is not null)
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [""] = [result.Error] },
                statusCode: StatusCodes.Status400BadRequest);

        using var doc = JsonDocument.Parse(result.OutputJson!);
        return Results.Ok(new PreviewResponse(doc.RootElement.Clone()));
    }
}

internal sealed record SourceContractPreviewRequest(
    JsonElement? Schema,
    JsonElement Mapping,
    JsonElement SampleInput,
    JsonElement? SampleContext);
