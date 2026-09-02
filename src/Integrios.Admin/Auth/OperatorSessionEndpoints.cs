using Integrios.Application.Identity;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Integrios.Admin.Auth;

/// The browser's own surface: start a sign-in, end a session, and read who is signed in. These are
/// not `/admin` capability endpoints and are excluded from the dashboard's SPA fallback.
public static class OperatorSessionEndpoints
{
    public const string BootstrapPath = "/auth/session";
    public const string LoginPath = "/auth/login";
    public const string LogoutPath = "/auth/logout";

    public static void MapOperatorSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(LoginPath, StartSignIn).WithName(nameof(StartSignIn));
        app.MapPost(LogoutPath, SignOutOperator).WithName(nameof(SignOutOperator));
        app.MapGet(BootstrapPath, GetSession).WithName(nameof(GetSession));
    }

    private static IResult StartSignIn([FromQuery(Name = "return_to")] string? returnTo) =>
        Results.Challenge(
            new AuthenticationProperties { RedirectUri = LocalReturnPath(returnTo) },
            [OperatorAuthentication.OidcScheme]);

    /// Removes the application cookie. The provider session is deliberately untouched: signing the
    /// person out of their identity provider everywhere is not this dashboard's decision.
    private static IResult SignOutOperator() =>
        Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme]);

    /// The one safe request the SPA makes before any mutation. It reports the signed-in User and
    /// issues the antiforgery token every unsafe cookie-authenticated request must echo.
    private static async Task<IResult> GetSession(
        HttpContext context,
        IMediator mediator,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        Guid? userId = context.User.UserId();
        if (userId is null)
            return Results.Unauthorized();

        OperatorUserDto? user = await mediator.Send(new GetOperatorUserQuery(userId.Value), cancellationToken);
        if (user is null)
            return Results.Unauthorized();

        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new OperatorSessionResponse(
            user.UserId,
            user.DisplayName,
            user.Email,
            tokens.RequestToken!,
            tokens.HeaderName!));
    }

    /// Keeps the browser off a guessable absolute redirect: only a same-origin path is honoured.
    private static string LocalReturnPath(string? returnTo) =>
        !string.IsNullOrWhiteSpace(returnTo)
        && returnTo.StartsWith('/')
        && !returnTo.StartsWith("//", StringComparison.Ordinal)
            ? returnTo
            : "/";
}

public sealed record OperatorSessionResponse(
    Guid UserId,
    string DisplayName,
    string? Email,
    string AntiforgeryToken,
    string AntiforgeryHeaderName);
