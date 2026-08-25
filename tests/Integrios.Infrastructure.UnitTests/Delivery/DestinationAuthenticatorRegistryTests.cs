using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Infrastructure.UnitTests;

public sealed class DestinationAuthenticatorRegistryTests
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    [Fact]
    public void Registry_ResolvesSchemeNames_CaseInsensitively()
    {
        IDestinationAuthenticatorRegistry registry = CreateRegistry();

        IDestinationAuthenticator handler = registry.GetRequired("API_KEY_HEADER");

        handler.ShouldBeOfType<ApiKeyHeaderAuthenticator>();
    }

    [Fact]
    public void Registry_GetRequired_ThrowsForUnknownScheme()
    {
        IDestinationAuthenticatorRegistry registry = CreateRegistry();

        var error = Should.Throw<DeliveryConfigurationException>(() => registry.GetRequired("nope"));

        error.Message.ShouldContain("Unknown auth scheme 'nope'.", Case.Sensitive);
    }

    [Fact]
    public void ApiKeyHeaderHandler_AppliesConfiguredHeader()
    {
        var handler = new ApiKeyHeaderAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonElement config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

        handler.Apply(headers, config, new Dictionary<string, string> { ["api_key"] = "secret-value" });

        headers["X-Api-Key"].ShouldBe("secret-value");
    }

    [Fact]
    public void BearerTokenHandler_AppliesAuthorizationHeader()
    {
        var handler = new BearerTokenAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        handler.Apply(headers, EmptyObject, new Dictionary<string, string> { ["token"] = "secret-token" });

        headers["Authorization"].ShouldBe("Bearer secret-token");
    }

    [Theory]
    [InlineData("secret-value\n")]
    [InlineData("secret\r\nvalue")]
    public void ApiKeyHeaderHandler_RejectsLineBreaksWithoutLeakingValue(string apiKey)
    {
        var handler = new ApiKeyHeaderAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonElement config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

        var error = Should.Throw<DeliveryConfigurationException>(
            () => handler.Apply(headers, config, new Dictionary<string, string> { ["api_key"] = apiKey }));

        error.Message.ShouldBe(
            "Auth secret field 'api_key' contains a line break, which is not permitted in an HTTP header value.");
        headers.ContainsKey("X-Api-Key").ShouldBeFalse();
    }

    [Theory]
    [InlineData("secret-token\n")]
    [InlineData("secret\r\ntoken")]
    public void BearerTokenHandler_RejectsLineBreaksWithoutLeakingValue(string token)
    {
        var handler = new BearerTokenAuthenticator();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var error = Should.Throw<DeliveryConfigurationException>(
            () => handler.Apply(headers, EmptyObject, new Dictionary<string, string> { ["token"] = token }));

        error.Message.ShouldBe(
            "Auth secret field 'token' contains a line break, which is not permitted in an HTTP header value.");
        headers.ContainsKey("Authorization").ShouldBeFalse();
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
