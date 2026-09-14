using System.Security.Claims;
using System.Text.Json.Serialization;
using Integrios.Application.Identity;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Integrios.Admin.Auth;

/// The browser's own surface: start a sign-in, end a session, and read who is signed in. These are
/// not `/admin` capability endpoints and are excluded from the dashboard's SPA fallback.
///
/// Reading the session and the available methods stays mapped even when the deployment configured
/// no human method at all. Those two answer truthfully without one -- no session, no methods -- and
/// a browser that reaches this host can then say so. Unmapping them instead made every such
/// deployment look to the browser like an Admin API that had stopped answering.
public static class OperatorSessionEndpoints
{
    public const string BootstrapPath = "/auth/session";
    public const string OptionsPath = "/auth/options";
    public const string LoginPath = "/auth/login";
    public const string PasswordLoginPath = "/auth/password/login";
    public const string LogoutPath = "/auth/logout";
    internal const long MaximumPasswordLoginBodySize = 16 * 1024;

    public static void MapOperatorSessionEndpoints(this IEndpointRouteBuilder app)
    {
        IConfiguration configuration = app.ServiceProvider.GetRequiredService<IConfiguration>();
        if (OperatorAuthentication.IsOidcConfigured(configuration))
            app.MapGet(LoginPath, StartSignIn).WithName(nameof(StartSignIn));
        if (OperatorAuthentication.IsPasswordEnabled(configuration))
        {
            app.MapPost(PasswordLoginPath, SignInWithPassword)
                .WithName(nameof(SignInWithPassword))
                .WithMetadata(new RequestSizeLimitAttribute(MaximumPasswordLoginBodySize))
                .Produces<OperatorPasswordLoginResponse>()
                .Produces<OperatorPasswordLoginFailureResponse>(StatusCodes.Status401Unauthorized)
                .Produces<OperatorPasswordLoginFailureResponse>(StatusCodes.Status429TooManyRequests);
        }

        app.MapGet(OptionsPath, GetOptions)
            .WithName(nameof(GetOptions))
            .Produces<OperatorAuthenticationOptionsResponse>();
        // Unlike the two reads below, signing out addresses the cookie scheme, which is only
        // registered alongside a human method.
        if (OperatorAuthentication.IsHumanAuthenticationConfigured(configuration))
            app.MapPost(LogoutPath, SignOutOperator).WithName(nameof(SignOutOperator));
        app.MapGet(BootstrapPath, GetSession).WithName(nameof(GetSession)).Produces<OperatorSessionResponse>();
    }

    private static IResult StartSignIn([FromQuery(Name = "return_to")] string? returnTo) =>
        Results.Challenge(
            new AuthenticationProperties { RedirectUri = LocalReturnPath(returnTo) },
            [OperatorAuthentication.OidcScheme]);

    /// Removes the application cookie. The provider session is deliberately untouched: signing the
    /// person out of their identity provider everywhere is not this dashboard's decision.
    private static IResult SignOutOperator() =>
        Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/?signed_out=1" },
            [CookieAuthenticationDefaults.AuthenticationScheme]);

    private static IResult GetOptions(
        HttpContext context,
        IConfiguration configuration,
        IAntiforgery antiforgery)
    {
        bool oidcEnabled = OperatorAuthentication.IsOidcConfigured(configuration);
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new OperatorAuthenticationOptionsResponse(
            oidcEnabled,
            OperatorAuthentication.IsPasswordEnabled(configuration),
            oidcEnabled ? OperatorOidcOptions.FromConfiguration(configuration).DisplayName : null,
            tokens.RequestToken!,
            tokens.HeaderName!));
    }

    private static async Task<IResult> SignInWithPassword(
        OperatorPasswordLoginRequest request,
        HttpContext context,
        OperatorPasswordAuthenticator authenticator,
        OperatorPasswordRateLimiter rateLimiter,
        OperatorSessionOptions session,
        ILogger<OperatorPasswordAuthenticator> logger,
        CancellationToken cancellationToken)
    {
        const string invalidMessage = "Email or password is invalid.";
        bool requestWithinBounds = request.Email is { Length: <= 320 }
            && request.Password is { Length: <= PasswordCredentialRules.MaximumPasswordLength * 2 }
            && (request.ReturnTo is null or { Length: <= 2048 });
        string emailPartition = requestWithinBounds
            && PasswordCredentialRules.TryNormalizeEmail(
            request.Email,
            out _,
            out string normalizedEmail)
            ? normalizedEmail
            : "invalid";
        string peerPartition = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        OperatorPasswordRateLimitResult rateLimit = await rateLimiter.TryAcquireAsync(
            emailPartition,
            peerPartition,
            cancellationToken);
        if (rateLimit != OperatorPasswordRateLimitResult.Acquired)
        {
            LogPasswordSignIn(logger, "rate_limited", rateLimit);
            context.Response.Headers.RetryAfter = ((int)OperatorPasswordRateLimiter.Window.TotalSeconds).ToString();
            return Results.Json(
                new OperatorPasswordLoginFailureResponse("Too many sign-in attempts. Try again later."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!requestWithinBounds)
        {
            LogPasswordSignIn(logger, "invalid", rateLimit);
            return Results.Json(
                new OperatorPasswordLoginFailureResponse(invalidMessage),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        OperatorPasswordAuthentication? authenticated = await authenticator.AuthenticateAsync(
            request.Email,
            request.Password,
            cancellationToken);
        if (authenticated is null)
        {
            LogPasswordSignIn(logger, "invalid", rateLimit);
            return Results.Json(
                new OperatorPasswordLoginFailureResponse(invalidMessage),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, authenticated.UserId.ToString()),
                new Claim(OperatorAuthentication.UserIdClaim, authenticated.UserId.ToString()),
                new Claim(ClaimTypes.Name, authenticated.DisplayName),
                new Claim(OperatorAuthentication.AuthenticationMethodClaim, OperatorAuthentication.PasswordMethod),
                new Claim(OperatorAuthentication.PasswordCredentialIdClaim, authenticated.CredentialId.ToString()),
                new Claim(
                    OperatorAuthentication.PasswordSessionRevisionClaim,
                    authenticated.SessionRevision.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                AllowRefresh = false,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(session.Lifetime),
                IsPersistent = true,
            });

        LogPasswordSignIn(logger, "succeeded", rateLimit);
        return Results.Ok(new OperatorPasswordLoginResponse(LocalReturnPath(request.ReturnTo)));
    }

    private static void LogPasswordSignIn(
        ILogger logger,
        string outcome,
        OperatorPasswordRateLimitResult rateLimit) =>
        logger.LogInformation(
            "Operator password sign-in finished with outcome {Outcome} and rate limit {RateLimit}.",
            outcome,
            rateLimit switch
            {
                OperatorPasswordRateLimitResult.Acquired => "none",
                OperatorPasswordRateLimitResult.EmailExceeded => "email",
                OperatorPasswordRateLimitResult.PeerExceeded => "peer",
                _ => "email_and_peer",
            });

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
            tokens.HeaderName!,
            tokens.FormFieldName));
    }

    /// Keeps the browser off a guessable absolute redirect: only a same-origin path is honoured.
    /// A leading "//" or "/\" is rejected because browsers resolve either as network-path (protocol-
    /// relative) references, which would send the redirect off-origin.
    internal static string LocalReturnPath(string? returnTo) =>
        IsLocalPath(returnTo) ? returnTo! : "/";

    private static bool IsLocalPath(string? returnTo)
    {
        if (string.IsNullOrEmpty(returnTo) || returnTo[0] != '/')
            return false;

        if (returnTo.Length == 1)
            return true;

        return returnTo[1] != '/' && returnTo[1] != '\\';
    }
}

public sealed record OperatorSessionResponse(
    Guid UserId,
    string DisplayName,
    string? Email,
    string AntiforgeryToken,
    string AntiforgeryHeaderName,
    string AntiforgeryFormFieldName);

public sealed record OperatorAuthenticationOptionsResponse(
    bool OidcEnabled,
    bool PasswordEnabled,
    string? OidcDisplayName,
    string AntiforgeryToken,
    string AntiforgeryHeaderName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorPasswordLoginRequest(
    string? Email,
    string? Password,
    string? ReturnTo);

public sealed record OperatorPasswordLoginResponse(string ReturnTo);

public sealed record OperatorPasswordLoginFailureResponse(string Message);
