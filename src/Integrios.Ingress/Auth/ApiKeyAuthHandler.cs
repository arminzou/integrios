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
        if (!TryParseHeader(Context, out var rawKey))
            return AuthenticateResult.NoResult();

        var keyHash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

        var result = await repository.FindActiveByKeyHashAsync(keyHash, Context.RequestAborted);
        if (result is null)
            return AuthenticateResult.Fail("Invalid API key.");

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

    // rawKey format: intg_<64hex>
    private static bool TryParseHeader(HttpContext context, out string rawKey)
    {
        rawKey = "";
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.OrdinalIgnoreCase))
            return false;

        rawKey = header[(SchemeName.Length + 1)..];
        return rawKey.StartsWith("intg_", StringComparison.Ordinal) && rawKey.Length > "intg_".Length;
    }
}
