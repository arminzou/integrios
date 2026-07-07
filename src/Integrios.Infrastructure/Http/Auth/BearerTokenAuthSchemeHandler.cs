using System.Net.Http.Headers;
using System.Text.Json;
using Integrios.Application.Abstractions.Auth;

namespace Integrios.Infrastructure.Http.Auth;

public sealed class BearerTokenAuthSchemeHandler : IAuthSchemeHandler
{
    public string Name => "bearer_token";
    public IReadOnlyList<string> RequiredConfigFields => [];
    public IReadOnlyList<string> RequiredSecretFields => ["token"];

    public void Apply(HttpRequestMessage request, JsonElement config, IReadOnlyDictionary<string, string> secrets)
    {
        _ = config;

        if (!secrets.TryGetValue("token", out string? token))
        {
            throw new InvalidOperationException("Auth secret field 'token' is required.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
