using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Integrios.Admin.Auth;

/// Validates antiforgery for every unsafe request that is authenticated by the browser session
/// cookie.
///
/// The OperatorKey path deliberately does not participate: a machine credential is sent
/// deliberately on each request and is not attached by the browser, so there is no cross-site
/// request to forge. SameSite is defense in depth, not the boundary.
public sealed class OperatorAntiforgeryMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresValidation(context))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                    title = "The antiforgery token is missing or invalid.",
                    status = StatusCodes.Status400BadRequest,
                });
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresValidation(HttpContext context) =>
        !SafeMethods.Contains(context.Request.Method)
        && context.User.Identity is { IsAuthenticated: true, AuthenticationType: CookieAuthenticationDefaults.AuthenticationScheme };
}
