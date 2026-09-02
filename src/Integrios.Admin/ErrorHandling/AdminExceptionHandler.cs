using Integrios.Application.Authoring;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Authoring.Connectors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Integrios.Admin.ErrorHandling;

public sealed class AdminExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            AuthoringValidationException validation => ValidationProblem(validation),
            DuplicateResourceException => Problem(StatusCodes.Status409Conflict, exception.Message),
            ConnectionAuthoringConflictException => Problem(StatusCodes.Status409Conflict, exception.Message),
            ConnectorVersionConflictException => Problem(StatusCodes.Status409Conflict, exception.Message),
            InvalidCursorException or InvalidListFilterException => Problem(StatusCodes.Status400BadRequest, exception.Message),
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

    private static HttpValidationProblemDetails ValidationProblem(AuthoringValidationException exception) => new(
        new Dictionary<string, string[]> { [exception.Field] = [exception.Message] })
    {
        Status = StatusCodes.Status422UnprocessableEntity,
        Title = "One or more validation errors occurred."
    };
}
