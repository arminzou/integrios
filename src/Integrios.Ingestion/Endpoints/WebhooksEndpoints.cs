using Integrios.Application.Events;
using MediatR;

namespace Integrios.Ingestion.Endpoints;

public sealed class WebhooksEndpoints : IEndpointGroup
{
    // The exact bound is a platform choice, not a provider contract; 1 MiB comfortably covers
    // GitHub's default webhook payloads while keeping intake memory use predictable.
    private const int MaxBodyBytes = 1_048_576;

    public string Prefix => "/webhooks";

    public void Map(RouteGroupBuilder group)
    {
        // No TenantApiKey authentication: a provider webhook cannot carry an Integrios credential.
        // Source verification (HMAC over the raw body) is the trust boundary here instead.
        group.MapPost(ReceiveWebhook, "/{connectorKey}/{endpointId:guid}");
    }

    private static async Task<IResult> ReceiveWebhook(
        string connectorKey,
        Guid endpointId,
        HttpContext httpContext,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (httpContext.Request.ContentLength is { } contentLength && contentLength > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        byte[]? rawBody = await ReadBoundedBodyAsync(httpContext.Request.Body, MaxBodyBytes, cancellationToken);
        if (rawBody is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in httpContext.Request.Headers)
            headers[header.Key] = header.Value.ToString();

        IngestEventResult result = await mediator.Send(
            new AcceptVerifiedWebhookCommand(
                connectorKey,
                endpointId,
                httpContext.Request.ContentType,
                headers,
                rawBody),
            cancellationToken);

        return Results.Accepted($"/events/{result.EventId}", result);
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
