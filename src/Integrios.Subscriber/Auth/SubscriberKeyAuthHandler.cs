using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Integrios.Application.Subscriber;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Integrios.Subscriber.Auth;

public sealed class SubscriberKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IActiveSubscriberKeyLookup lookup,
    ISubscriberKeyUseRecorder useRecorder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SubscriberKey";
    public const string TenantIdClaim = "tenant_id";
    public const string DestinationIdClaim = "destination_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        string rawKey = header[bearer.Length..];
        if (!rawKey.StartsWith("intg_sub_", StringComparison.Ordinal) || rawKey.Length != 73)
            return AuthenticateResult.Fail("Invalid subscriber key.");

        string hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        SubscriberIdentity? subscriber = await lookup.FindActiveByKeyHashAsync(hash, Context.RequestAborted);
        if (subscriber is null)
            return AuthenticateResult.Fail("Invalid subscriber key.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (SubscriberKeyUse.ShouldRecord(subscriber.LastUsedAt, now))
            await useRecorder.RecordUseAsync(subscriber.KeyId, now, Context.RequestAborted);

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, subscriber.KeyId.ToString()),
            new(TenantIdClaim, subscriber.TenantId.ToString()),
            new(DestinationIdClaim, subscriber.DestinationId.ToString()),
        ];
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Task.CompletedTask;
    }
}
