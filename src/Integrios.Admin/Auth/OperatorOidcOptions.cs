using Microsoft.Extensions.Configuration;

namespace Integrios.Admin.Auth;

/// Provider-neutral OpenID Connect configuration. The Admin host owns the protocol boundary; no
/// provider name reaches the Domain or Application contracts.
public sealed record OperatorOidcOptions
{
    public const string SectionKey = "Integrios:Admin:Oidc";
    public const string AuthorityKey = SectionKey + ":Authority";

    public required string Authority { get; init; }
    public required string ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required string CallbackPath { get; init; }
    public required string SignedOutCallbackPath { get; init; }
    public required bool RequireHttpsMetadata { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }

    public static OperatorOidcOptions FromConfiguration(IConfiguration configuration)
    {
        string authority = Required(configuration, AuthorityKey);
        string clientId = Required(configuration, SectionKey + ":ClientId");
        string? scopes = configuration[SectionKey + ":Scopes"];

        return new OperatorOidcOptions
        {
            Authority = authority,
            ClientId = clientId,
            ClientSecret = configuration[SectionKey + ":ClientSecret"],
            CallbackPath = configuration[SectionKey + ":CallbackPath"] ?? "/auth/callback",
            SignedOutCallbackPath = configuration[SectionKey + ":SignedOutCallbackPath"] ?? "/auth/signed-out",
            // Only a local development provider may be reached over plain HTTP.
            RequireHttpsMetadata = configuration.GetValue<bool?>(SectionKey + ":RequireHttpsMetadata") ?? true,
            Scopes = string.IsNullOrWhiteSpace(scopes)
                ? ["openid", "profile", "email"]
                : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }

    private static string Required(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is required when an identity provider is configured.")
            : value;
    }
}
