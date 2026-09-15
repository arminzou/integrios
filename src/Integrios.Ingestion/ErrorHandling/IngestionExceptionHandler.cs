using Integrios.Application.Ingestion;
using Integrios.Application.Secrets;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Integrios.Ingestion.ErrorHandling;

public sealed class IngestionExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            EventAcceptanceException => Problem(StatusCodes.Status422UnprocessableEntity, exception.Message),
            SourceEndpointNotFoundException => Problem(StatusCodes.Status404NotFound, exception.Message),
            SourceVerificationException => Problem(StatusCodes.Status401Unauthorized, exception.Message),
            WebhookPayloadException => Problem(StatusCodes.Status400BadRequest, exception.Message),
            // A Source naming a secret that does not resolve is misconfigured, not malformed: the
            // request is well formed, and retrying succeeds once the secret lands, so a 5xx is the
            // honest status and the one that tells a provider to try again. Mapped rather than left
            // unhandled so the status is a decision instead of a fallback, and carrying a fixed
            // detail because the reference belongs in Ingestion's log, not in a response an
            // external caller reads.
            SecretResolutionException => Problem(
                StatusCodes.Status500InternalServerError,
                "The Source could not be verified."),
            BadHttpRequestException badRequest => Problem(badRequest.StatusCode, "The request is invalid."),
            _ => null
        };

        if (problem is null)
            return false;

        httpContext.Response.StatusCode = problem.Status!.Value;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
        return true;
    }

    private static ProblemDetails Problem(int statusCode, string detail) => new()
    {
        Status = statusCode,
        Title = ReasonPhrases.GetReasonPhrase(statusCode),
        Detail = detail
    };
}
