using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Secrets;

namespace Integrios.Infrastructure.Delivery;

internal sealed class BearerTokenAuthenticator : IDestinationAuthenticator
{
    public string Name => "bearer_token";
    public IReadOnlyList<string> RequiredConfigFields => [];
    public IReadOnlyList<string> RequiredSecretFields => ["token"];
    public IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config) => ["Authorization"];

    public void Apply(IDictionary<string, string> headers, JsonElement config, IReadOnlyDictionary<string, string> secrets)
    {
        _ = config;

        if (!secrets.TryGetValue("token", out string? token))
        {
            throw new DeliveryConfigurationException("Auth secret field 'token' is required.");
        }

        SecretValueValidator.EnsureHeaderSafe(token, "token");
        headers.Add("Authorization", $"Bearer {token}");
    }
}
