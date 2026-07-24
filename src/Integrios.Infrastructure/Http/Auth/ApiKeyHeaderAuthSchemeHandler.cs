using System.Text.Json;
using Integrios.Application.Abstractions.Auth;

namespace Integrios.Infrastructure.Http.Auth;

public sealed class ApiKeyHeaderAuthSchemeHandler : IAuthSchemeHandler
{
    public string Name => "api_key_header";
    public IReadOnlyList<string> RequiredConfigFields => ["header_name"];
    public IReadOnlyList<string> RequiredSecretFields => ["api_key"];

    public void Apply(HttpRequestMessage request, JsonElement config, IReadOnlyDictionary<string, string> secrets)
    {
        string headerName = config.GetProperty("header_name").GetString()
            ?? throw new InvalidOperationException("Auth config field 'header_name' is required.");

        if (!secrets.TryGetValue("api_key", out string? apiKey))
        {
            throw new InvalidOperationException("Auth secret field 'api_key' is required.");
        }

        SecretValueValidator.EnsureHeaderSafe(apiKey, "api_key");
        request.Headers.TryAddWithoutValidation(headerName, apiKey);
    }
}
