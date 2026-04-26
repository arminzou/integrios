using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Integrios.Application.Abstractions;
using Integrios.Domain.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Integrios.Admin.Auth;

public sealed class AdminKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAdminKeyRepository repository)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AdminKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryParseHeader(Context, out var publicKey, out var secret))
            return AuthenticateResult.NoResult();

        AdminKey? adminKey = await repository.FindActiveByPublicKeyAsync(publicKey, Context.RequestAborted);
        if (adminKey is null || !VerifySecret(secret, adminKey.SecretHash))
            return AuthenticateResult.Fail("Invalid admin key or secret.");

        Context.SetAdminPrincipal(new AdminPrincipal { AdminKey = adminKey });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, adminKey.Id.ToString()),
            new("admin_key_id", adminKey.Id.ToString()),
        };
        if (adminKey.TenantId is { } tenantId)
            claims.Add(new Claim("tenant_id", tenantId.ToString()));

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
