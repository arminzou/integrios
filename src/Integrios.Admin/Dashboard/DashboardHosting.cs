using Integrios.Admin.Auth;

namespace Integrios.Admin.Dashboard;

/// Serves the Operator dashboard from the Admin host itself.
///
/// The dashboard and the API share one origin, which is why the product configures no CORS policy:
/// there is no cross-origin request to permit. Browser routes fall back to the SPA shell, but only
/// browser routes: an unknown path under the API, authentication, OpenAPI, or the operational
/// endpoints must keep its real non-SPA response instead of being answered with HTML.
public static class DashboardHosting
{
    public const string ShellFile = "index.html";

    /// Prefixes the SPA shell never answers for. A request under one of these that matches no
    /// endpoint is a genuine 404 or 401, and dressing it up as the dashboard would hide a broken
    /// client behind a page that looks fine.
    private static readonly string[] NonBrowserPrefixes =
    [
        "/admin",
        "/auth",
        "/openapi",
        "/health",
        "/ready",
        "/metrics",
    ];

    public static bool IsDashboardAvailable(IWebHostEnvironment environment) =>
        File.Exists(Path.Combine(environment.WebRootPath ?? string.Empty, ShellFile));

    public static bool IsBrowserRoute(HttpContext context) =>
        !NonBrowserPrefixes.Any(prefix =>
            context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// The dashboard needs a browser sign-in path to ever reach a signed-in state, so it stays
    /// unmapped without one: serving the shell while `/auth/session` has no route would hand the
    /// browser a page that can never bootstrap.
    public static void MapDashboard(this WebApplication app)
    {
        if (!IsDashboardAvailable(app.Environment) || !OperatorAuthentication.IsOidcConfigured(app.Configuration))
            return;

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile(ShellFile).Add(builder =>
        {
            RequestDelegate shell = builder.RequestDelegate!;
            builder.RequestDelegate = async context =>
            {
                if (!IsBrowserRoute(context))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await shell(context);
            };
        });
    }
}
