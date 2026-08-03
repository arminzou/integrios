using Integrios.Application.Common.Exceptions;
using Integrios.Application.Connections;
using Integrios.Application.Integrations;
using Integrios.Application.Subscriptions;
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
            TenantRequestValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            ConnectionRequestValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            IntegrationManifestValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            IntegrationVersionConflictException => (StatusCodes.Status409Conflict, exception.Message),
            TopicRequestValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            SubscriptionRequestValidationException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
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
