using System.Text.Json;
using Integrios.Application.Delivery;

namespace Integrios.Tests.Shared;

public sealed class FakeDestinationAuthenticatorRegistry : IDestinationAuthenticatorRegistry
{
    private readonly IReadOnlyDictionary<string, IDestinationAuthenticator> _handlers;

    public FakeDestinationAuthenticatorRegistry(params IDestinationAuthenticator[] handlers)
        => _handlers = handlers.ToDictionary(h => h.Name);

    public IDestinationAuthenticator GetRequired(string scheme)
        => TryGet(scheme, out IDestinationAuthenticator? handler)
            ? handler
            : throw new DeliveryConfigurationException($"No authenticator registered for scheme '{scheme}'.");

    public bool TryGet(string scheme, out IDestinationAuthenticator handler)
        => _handlers.TryGetValue(scheme, out handler!);
}

public sealed class FakeApiKeyHeaderAuthenticator : IDestinationAuthenticator
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
            throw new DeliveryConfigurationException("Auth secret field 'api_key' is required.");

        headers.Add(headerName, apiKey);
    }
}

public sealed class FakeBearerTokenAuthenticator : IDestinationAuthenticator
{
    public string Name => "bearer_token";
    public IReadOnlyList<string> RequiredConfigFields => [];
    public IReadOnlyList<string> RequiredSecretFields => ["token"];
    public IReadOnlyList<string> GetOwnedHeaderNames(JsonElement config) => ["Authorization"];

    public void Apply(IDictionary<string, string> headers, JsonElement config, IReadOnlyDictionary<string, string> secrets)
    {
        _ = config;

        if (!secrets.TryGetValue("token", out string? token))
            throw new DeliveryConfigurationException("Auth secret field 'token' is required.");

        headers.Add("Authorization", $"Bearer {token}");
    }
}
