using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Integrios.Admin.Auth;

public sealed class AdminTokenEndpointFilter(IOptions<AdminAuthOptions> options) : IEndpointFilter
{
    private const string Scheme = "Bearer";
    private const string SchemePrefix = Scheme + " ";

    private readonly byte[] _expectedToken = Encoding.UTF8.GetBytes(options.Value.Token ?? "");

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (_expectedToken.Length == 0)
            return Results.Problem("Admin token is not configured.", statusCode: StatusCodes.Status500InternalServerError);

        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
            return Reject(http);

        byte[] presented = Encoding.UTF8.GetBytes(header[SchemePrefix.Length..]);
        if (!CryptographicOperations.FixedTimeEquals(presented, _expectedToken))
            return Reject(http);

        return await next(context);
    }

    private static IResult Reject(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = Scheme;
        return Results.Unauthorized();
    }
}
