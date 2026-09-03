using Integrios.Admin.Auth;
using Microsoft.AspNetCore.StaticFiles;

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

    /// Where the frontend build emits its bundles. Every file below it carries a content hash in
    /// its own name, so the URL identifies exactly one version of one file, forever.
    private const string HashedAssetPrefix = "/assets";

    /// Without an explicit policy a browser is free to guess how long a response stays fresh, and
    /// it guesses from the file's age rather than asking. That is tolerable for a bundle and wrong
    /// for the shell: the shell's URL never changes while its content must, because it names the
    /// bundle hash to load. A stale shell therefore does not render an old dashboard, it renders
    /// nothing — it keeps requesting a hash the deployment no longer has.
    ///
    /// So the two are pinned in opposite directions. A hashed bundle can never change under its
    /// own URL and is cached for a year without revalidating; the shell is always revalidated,
    /// which costs one conditional request that answers 304 whenever it is unchanged.
    private static void SetCacheControl(StaticFileResponseContext context)
    {
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments(HashedAssetPrefix, StringComparison.OrdinalIgnoreCase)
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    }

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

        // The same options serve both paths so the shell is governed by one rule whether it is
        // reached directly or through the browser-route fallback.
        var files = new StaticFileOptions { OnPrepareResponse = SetCacheControl };

        app.UseDefaultFiles();
        app.UseStaticFiles(files);
        app.MapFallbackToFile(ShellFile, files).Add(builder =>
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
