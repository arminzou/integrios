using System.Security.Cryptography;
using System.Text;
using Integrios.Api.Infrastructure.Data.Tenants;

namespace Integrios.Api.Auth;

public sealed class ApiKeyEndpointFilter(IApiCredentialRepository repository) : IEndpointFilter
{
    private const string Scheme = "ApiKey";
    private const string SchemePrefix = Scheme + " ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!TryParseHeader(http, out var keyId, out var secret))
            return Reject(http);

        var result = await repository.FindActiveByKeyIdAsync(keyId, http.RequestAborted);
        if (result is null || !VerifySecret(secret, result.Value.Credential.SecretHash))
            return Reject(http);

        http.SetTenantContext(new TenantContext
        {
            Tenant = result.Value.Tenant,
            Credential = result.Value.Credential,
        });

        return await next(context);
    }

    private static IResult Reject(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = Scheme;
        return Results.Unauthorized();
    }

    private static bool TryParseHeader(HttpContext context, out string keyId, out string secret)
    {
        keyId = secret = "";
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var token = header[SchemePrefix.Length..];
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1)
            return false;

        keyId = token[..colon];
        secret = token[(colon + 1)..];
        return true;
    }

    // secretHash format: sha256:<lowercase-hex>
    private static bool VerifySecret(string secret, string secretHash)
    {
        if (!secretHash.StartsWith("sha256:", StringComparison.Ordinal))
            return false;

        byte[] expected;
        try { expected = Convert.FromHexString(secretHash["sha256:".Length..]); }
        catch (FormatException) { return false; }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
