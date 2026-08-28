using Integrios.Application.Ingestion;
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
