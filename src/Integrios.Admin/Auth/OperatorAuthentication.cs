using System.Security.Claims;
using Integrios.Application.Identity;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Integrios.Admin.Auth;

/// Composes machine OperatorKey authentication and human User-cookie authentication. A human cookie
/// may originate from OpenID Connect or a Password credential without merging those identities.
public static class OperatorAuthentication
{
    public const string PolicyName = "Operator";
    public const string UserIdClaim = "integrios_user_id";
    public const string AuthenticationMethodClaim = "integrios_auth_method";
    public const string PasswordCredentialIdClaim = "integrios_password_credential_id";
    public const string PasswordSessionRevisionClaim = "integrios_password_session_revision";
    public const string OidcMethod = "oidc";
    public const string PasswordMethod = "password";
    public const string OidcScheme = "OperatorOidc";
    public const string SelectorScheme = "OperatorCredential";

    /// True when the deployment configured an identity provider. Without one, Admin stays
    /// machine-only rather than starting with a browser path that cannot complete a sign-in.
    public static bool IsOidcConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[OperatorOidcOptions.AuthorityKey]);

    public static bool IsPasswordEnabled(IConfiguration configuration) =>
        OperatorPasswordOptions.FromConfiguration(configuration).Enabled;

    public static bool IsHumanAuthenticationConfigured(IConfiguration configuration) =>
        IsOidcConfigured(configuration) || IsPasswordEnabled(configuration);

    public static IServiceCollection AddOperatorAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        OperatorSessionOptions session = OperatorSessionOptions.FromConfiguration(configuration);
        services.AddSingleton(session);
        OperatorPasswordOptions password = OperatorPasswordOptions.FromConfiguration(configuration);
        services.AddSingleton(password);

        bool oidcConfigured = IsOidcConfigured(configuration);
        bool humanConfigured = oidcConfigured || password.Enabled;

        // One default scheme decides per request which credential is in play, so HttpContext.User is
        // already the right principal by the time antiforgery and authorization run. Without this,
        // the default scheme would authenticate only machine calls and a browser request would
        // arrive at the pipeline anonymous.
        AuthenticationBuilder authentication = services
            .AddAuthentication(humanConfigured ? SelectorScheme : OperatorKeyAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, OperatorKeyAuthHandler>(
                OperatorKeyAuthHandler.SchemeName, _ => { });

        if (humanConfigured)
        {
            authentication.AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString()
                        .StartsWith(OperatorKeyAuthHandler.SchemeName + " ", StringComparison.OrdinalIgnoreCase)
                        ? OperatorKeyAuthHandler.SchemeName
                        : CookieAuthenticationDefaults.AuthenticationScheme);
        }

        if (humanConfigured)
        {
            // Matches RequireHttpsMetadata: secure-only except for a local plaintext-HTTP deployment
            // that explicitly opted out. A hardcoded Always here would make the browser session
            // uncheckable behind plain HTTP -- antiforgery fails closed with a 500 rather than
            // silently accepting a forgeable request, so this must track the same knob.
            bool requireHttpsCookies = configuration.GetValue<bool?>(
                OperatorOidcOptions.SectionKey + ":RequireHttpsMetadata") ?? true;
            CookieSecurePolicy cookieSecurePolicy = requireHttpsCookies
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            authentication.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = OperatorSessionOptions.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = cookieSecurePolicy;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = session.Lifetime;
                // Fixed, not sliding: the lifetime is the deprovisioning bound, so activity must not
                // extend it.
                options.SlidingExpiration = false;
                options.LoginPath = "/auth/login";
                options.LogoutPath = "/auth/logout";
                options.AccessDeniedPath = "/auth/login";
                // The browser talks to the same origin as the API, so an unauthenticated API call
                // must answer 401 rather than redirect an XHR to the provider.
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/admin"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = ValidatePasswordSessionAsync;
            });
        }

        if (password.Enabled)
        {
            services.AddScoped<OperatorPasswordAuthenticator>();
            services.AddSingleton<OperatorPasswordRateLimiter>();
        }

        if (oidcConfigured)
        {
            OperatorOidcOptions oidc = OperatorOidcOptions.FromConfiguration(configuration);
            services.AddSingleton(oidc);
            bool requireHttpsCookies = configuration.GetValue<bool?>(
                OperatorOidcOptions.SectionKey + ":RequireHttpsMetadata") ?? true;
            CookieSecurePolicy cookieSecurePolicy = requireHttpsCookies
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            authentication.AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                // The provider must redirect the code back, not POST it. A form_post callback is a
                // cross-site POST, which a SameSite=Strict session and Lax correlation cookies
                // would not accompany; a redirect keeps the whole browser path same-site.
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = cookieSecurePolicy;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = cookieSecurePolicy;
                options.CallbackPath = oidc.CallbackPath;
                options.SignedOutCallbackPath = oidc.SignedOutCallbackPath;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                // Browser JavaScript never receives a provider token, so nothing keeps them.
                options.SaveTokens = false;
                options.Scope.Clear();
                foreach (string scope in oidc.Scopes)
                    options.Scope.Add(scope);

                options.Events.OnTokenValidated = ResolveOperatorUserAsync;
                options.Events.OnAccessDenied = context =>
                {
                    context.Response.Redirect("/?error=access_denied");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        }

        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder();
            policy.RequireAuthenticatedUser();
            policy.AddAuthenticationSchemes(humanConfigured
                ? [SelectorScheme]
                : [OperatorKeyAuthHandler.SchemeName]);
            options.AddPolicy(PolicyName, policy.Build());
            options.DefaultPolicy = options.GetPolicy(PolicyName)!;
        });

        return services;
    }

    /// Exchanges the validated provider identity for the Integrios User the cookie will carry. The
    /// cookie's authoritative subject is User.Id, never the provider's subject.
    private static async Task ResolveOperatorUserAsync(TokenValidatedContext context)
    {
        ClaimsPrincipal principal = context.Principal
            ?? throw new InvalidOperationException("The identity provider returned no principal.");

        string? issuer = principal.FindFirst("iss")?.Value
            ?? context.SecurityToken?.Issuer;
        string? subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            context.Fail("The identity provider returned no issuer and subject pair.");
            return;
        }

        IMediator mediator = context.HttpContext.RequestServices.GetRequiredService<IMediator>();
        OperatorUserDto user = await mediator.Send(
            new ResolveOperatorIdentityCommand(
                issuer,
                subject,
                new OperatorIdentityClaims(
                    principal.FindFirst("name")?.Value ?? principal.FindFirst("preferred_username")?.Value,
                    principal.FindFirst("email")?.Value)),
            context.HttpContext.RequestAborted);

        // Replace the provider principal outright: the session carries the Integrios User and no
        // provider claims beyond the display name it needs.
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(UserIdClaim, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.DisplayName),
                new Claim(AuthenticationMethodClaim, OidcMethod),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        context.Principal = new ClaimsPrincipal(identity);
    }

    private static async Task ValidatePasswordSessionAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.FindFirst(AuthenticationMethodClaim)?.Value != PasswordMethod)
            return;

        OperatorPasswordOptions password = context.HttpContext.RequestServices
            .GetRequiredService<OperatorPasswordOptions>();
        Guid? userId = context.Principal.UserId();
        bool hasCredentialId = Guid.TryParse(
            context.Principal.FindFirst(PasswordCredentialIdClaim)?.Value,
            out Guid credentialId);
        bool hasSessionRevision = int.TryParse(
            context.Principal.FindFirst(PasswordSessionRevisionClaim)?.Value,
            out int sessionRevision);
        bool validClaims = password.Enabled
            && userId is not null
            && hasCredentialId
            && hasSessionRevision;
        if (!validClaims)
        {
            context.RejectPrincipal();
            return;
        }

        IPasswordAuthenticationStore store = context.HttpContext.RequestServices
            .GetRequiredService<IPasswordAuthenticationStore>();
        if (!await store.IsCurrentSessionAsync(
                credentialId,
                userId!.Value,
                sessionRevision,
                context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
        }
    }

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out Guid userId) ? userId : null;
}
