using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Secrets;

namespace Integrios.Infrastructure.Delivery;

internal sealed class ApiKeyHeaderAuthenticator : IDestinationAuthenticator
{
    public string Name => "api_key_header";
    public IReadOnlyList<string> RequiredConfigFields => ["header_name"];
    public IReadOnlyList<string> RequiredSecretFields => ["api_key"];

    public IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config)
    {
        string headerName = config.GetProperty("header_name").GetString()
            ?? throw new DeliveryConfigurationException("Auth config field 'header_name' is required.");
        return [headerName];
    }

    public void Apply(IDictionary<string, string> headers, JsonElement config, IReadOnlyDictionary<string, string> secrets)
    {
        string headerName = GetOwnedHeaderNames(config)[0];

        if (!secrets.TryGetValue("api_key", out string? apiKey))
        {
            throw new DeliveryConfigurationException("Auth secret field 'api_key' is required.");
        }

        SecretValueValidator.EnsureHeaderSafe(apiKey, "api_key");
        headers.Add(headerName, apiKey);
    }
}
