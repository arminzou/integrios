using Integrios.Application.Common.Exceptions;
using Integrios.Application.Connections;
using Integrios.Application.Connectors;
using Integrios.Application.Subscriptions;
using Integrios.Application.Sources;
using Integrios.Application.Tenants;
using Integrios.Application.Topics;
using Microsoft.AspNetCore.Diagnostics;

namespace Integrios.Admin.ErrorHandling;

public sealed class AdminExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        (int StatusCode, string Message)? error = exception switch
        {
            DuplicateResourceException => (StatusCodes.Status409Conflict, exception.Message),
            ConnectionAuthoringConflictException => (StatusCodes.Status409Conflict, exception.Message),
            TenantValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            ConnectionValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            ConnectorManifestValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            ConnectorVersionConflictException => (StatusCodes.Status409Conflict, exception.Message),
            TopicValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            SourceValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            SubscriptionValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "The request body is invalid."),
            _ => null
        };

        if (error is null)
            return false;

        httpContext.Response.StatusCode = error.Value.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(
            new ErrorResponse(error.Value.Message),
            cancellationToken);
        return true;
    }

    private sealed record ErrorResponse(string Error);
}
