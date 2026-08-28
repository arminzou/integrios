using System.Text.Json;
using Integrios.Application.Authoring.Subscriptions;
using MediatR;

namespace Integrios.Admin.Endpoints;

public sealed class TransformEndpoints : IEndpointGroup
{
    public string Prefix => "/transform";

    public void Map(RouteGroupBuilder group)
    {
        group.MapPost(PreviewMapping, "/preview");
    }

    // Stateless dry-run: evaluate a transform against a sample payload so an author can see the
    // output before saving. No tenant data is read, so any authenticated admin may call it.
    private static async Task<IResult> PreviewMapping(
        TransformPreviewRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        PreviewMappingResult result = await mediator.Send(
            new PreviewMappingQuery(request.Transform, request.SampleInput, request.SampleContext),
            cancellationToken);
        if (result.Error is not null)
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [""] = [result.Error] },
                statusCode: StatusCodes.Status400BadRequest);

        using var doc = JsonDocument.Parse(result.OutputJson!);
        return Results.Ok(new { output = doc.RootElement.Clone() });
    }
}

internal sealed record TransformPreviewRequest(
    JsonElement Transform,
    JsonElement SampleInput,
    JsonElement? SampleContext);
