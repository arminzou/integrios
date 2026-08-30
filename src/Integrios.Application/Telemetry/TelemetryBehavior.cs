using System.Diagnostics;
using Integrios.Application.Ingestion;
using MediatR;

namespace Integrios.Application.Telemetry;

public sealed class TelemetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using Activity? activity = request is IngestEventCommand
            or AcceptVerifiedWebhookCommand
            or AcceptQueueMessageCommand
            ? ActivitySources.StartRootSpan("event.accept")
            : ActivitySources.Application.StartActivity(typeof(TRequest).Name);

        try
        {
            return await next(cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.AddException(ex);
            throw;
        }
    }
}
