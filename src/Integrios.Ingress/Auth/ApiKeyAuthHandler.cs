using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Integrios.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Integrios.Ingress.Auth;

public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyRepository repository)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryParseHeader(Context, out var publicKey, out var secret))
            return AuthenticateResult.NoResult();

        var result = await repository.FindActiveByPublicKeyAsync(publicKey, Context.RequestAborted);
        if (result is null || !VerifySecret(secret, result.Value.ApiKey.SecretHash))
            return AuthenticateResult.Fail("Invalid API key or secret.");

        Context.SetTenantContext(new TenantContext
        {
            Tenant = result.Value.Tenant,
            ApiKey = result.Value.ApiKey,
        });

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, result.Value.Tenant.Id.ToString()) };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Task.CompletedTask;
    }

    private static bool TryParseHeader(HttpContext context, out string publicKey, out string secret)
    {
        publicKey = secret = "";
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.OrdinalIgnoreCase))
            return false;

        var token = header[(SchemeName.Length + 1)..];
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1)
            return false;

        publicKey = token[..colon];
        secret = token[(colon + 1)..];
        return true;
    }

    // secretHash format: sha256:<lowercase-hex>
    private static bool VerifySecret(string secret, string secretHash)
    {
        if (!secretHash.StartsWith("sha256:", StringComparison.Ordinal))
            return false;

        byte[] expected;
        try
        { expected = Convert.FromHexString(secretHash["sha256:".Length..]); }
        catch (FormatException) { return false; }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
