using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Worker.UnitTests;

public sealed class DestinationAuthenticatorRegistryTests
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    [Fact]
    public void Registry_ResolvesSchemeNames_CaseInsensitively()
    {
        IDestinationAuthenticatorRegistry registry = CreateRegistry();

        IDestinationAuthenticator handler = registry.GetRequired("API_KEY_HEADER");

        Assert.IsType<ApiKeyHeaderAuthenticator>(handler);
    }

    [Fact]
    public void Registry_GetRequired_ThrowsForUnknownScheme()
    {
        IDestinationAuthenticatorRegistry registry = CreateRegistry();

        var error = Assert.Throws<DeliveryConfigurationException>(() => registry.GetRequired("nope"));

        Assert.Contains("Unknown auth scheme 'nope'.", error.Message);
    }

    [Fact]
    public void ApiKeyHeaderHandler_AppliesConfiguredHeader()
    {
        var handler = new ApiKeyHeaderAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonElement config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

        handler.Apply(headers, config, new Dictionary<string, string> { ["api_key"] = "secret-value" });

        Assert.Equal("secret-value", headers["X-Api-Key"]);
    }

    [Fact]
    public void BearerTokenHandler_AppliesAuthorizationHeader()
    {
        var handler = new BearerTokenAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        handler.Apply(headers, EmptyObject, new Dictionary<string, string> { ["token"] = "secret-token" });

        Assert.Equal("Bearer secret-token", headers["Authorization"]);
    }

    [Theory]
    [InlineData("secret-value\n")]
    [InlineData("secret\r\nvalue")]
    public void ApiKeyHeaderHandler_RejectsLineBreaksWithoutLeakingValue(string apiKey)
    {
        var handler = new ApiKeyHeaderAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonElement config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

        var error = Assert.Throws<DeliveryConfigurationException>(
            () => handler.Apply(headers, config, new Dictionary<string, string> { ["api_key"] = apiKey }));

        Assert.Equal(
            "Auth secret field 'api_key' contains a line break, which is not permitted in an HTTP header value.",
            error.Message);
        Assert.False(headers.ContainsKey("X-Api-Key"));
    }

    [Theory]
    [InlineData("secret-token\n")]
    [InlineData("secret\r\ntoken")]
    public void BearerTokenHandler_RejectsLineBreaksWithoutLeakingValue(string token)
    {
        var handler = new BearerTokenAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var error = Assert.Throws<DeliveryConfigurationException>(
            () => handler.Apply(headers, EmptyObject, new Dictionary<string, string> { ["token"] = token }));

        Assert.Equal(
            "Auth secret field 'token' contains a line break, which is not permitted in an HTTP header value.",
            error.Message);
        Assert.False(headers.ContainsKey("Authorization"));
    }

    private static IDestinationAuthenticatorRegistry CreateRegistry()
    {
        return new DestinationAuthenticatorRegistry(
        [
            new ApiKeyHeaderAuthenticator(),
            new BearerTokenAuthenticator()
        ]);
    }
}
