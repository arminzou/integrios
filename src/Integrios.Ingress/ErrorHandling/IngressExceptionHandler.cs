using Integrios.Application.Events;
using Microsoft.AspNetCore.Diagnostics;

namespace Integrios.Ingress.ErrorHandling;

public sealed class IngressExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        (int StatusCode, string Message)? error = exception switch
        {
            EventAcceptanceException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
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
