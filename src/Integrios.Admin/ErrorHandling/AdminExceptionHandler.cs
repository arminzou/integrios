using System.Text.Json;
using Integrios.Application.Authoring;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Destinations;
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
            DestinationAuthoringConflictException => Problem(StatusCodes.Status409Conflict, exception.Message),
            ConnectorVersionConflictException => Problem(StatusCodes.Status409Conflict, exception.Message),
            InvalidCursorException or InvalidListFilterException => Problem(StatusCodes.Status400BadRequest, exception.Message),
            // An authoring update replaces the whole resource, so the serializer refusing an absent
            // member is the guard against an omitted field being read as "clear this". The schema
            // names which members are required; the message says what the verb means.
            BadHttpRequestException { InnerException: JsonException } => Problem(
                StatusCodes.Status400BadRequest,
                "The request body is malformed or is missing a required field. An update replaces the "
                + "whole resource, so send every field the schema marks required, using null to clear one."),
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
