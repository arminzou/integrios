using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Secrets;

namespace Integrios.Infrastructure.Auth;

public sealed class ApiKeyHeaderAuthSchemeHandler : IAuthSchemeHandler
{
    public string Name => "api_key_header";
    public IReadOnlyList<string> RequiredConfigFields => ["header_name"];
    public IReadOnlyList<string> RequiredSecretFields => ["api_key"];

    public void Apply(HttpRequestMessage request, JsonElement config, IReadOnlyDictionary<string, string> secrets)
    {
        string headerName = config.GetProperty("header_name").GetString()
            ?? throw new DeliveryConfigurationException("Auth config field 'header_name' is required.");

        if (!secrets.TryGetValue("api_key", out string? apiKey))
        {
            throw new DeliveryConfigurationException("Auth secret field 'api_key' is required.");
        }

        SecretValueValidator.EnsureHeaderSafe(apiKey, "api_key");
        request.Headers.TryAddWithoutValidation(headerName, apiKey);
    }
}
