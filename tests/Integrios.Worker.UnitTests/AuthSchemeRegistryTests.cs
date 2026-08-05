using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Auth;

namespace Integrios.Worker.UnitTests;

public sealed class AuthSchemeRegistryTests
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    [Fact]
    public void Registry_ResolvesSchemeNames_CaseInsensitively()
    {
        IAuthSchemeRegistry registry = CreateRegistry();

        IAuthSchemeHandler handler = registry.GetRequired("API_KEY_HEADER");

        Assert.IsType<ApiKeyHeaderAuthSchemeHandler>(handler);
    }

    [Fact]
    public void Registry_GetRequired_ThrowsForUnknownScheme()
    {
        IAuthSchemeRegistry registry = CreateRegistry();

        var error = Assert.Throws<DeliveryConfigurationException>(() => registry.GetRequired("nope"));

        Assert.Contains("Unknown auth scheme 'nope'.", error.Message);
    }

    [Fact]
    public void ApiKeyHeaderHandler_AppliesConfiguredHeader()
    {
        var handler = new ApiKeyHeaderAuthSchemeHandler();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://downstream.example");
        JsonElement config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

        handler.Apply(request, config, new Dictionary<string, string> { ["api_key"] = "secret-value" });

        Assert.True(request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values));
        Assert.Equal(["secret-value"], values);
    }

    [Fact]
    public void BearerTokenHandler_AppliesAuthorizationHeader()
    {
        var handler = new BearerTokenAuthSchemeHandler();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://downstream.example");

        handler.Apply(request, EmptyObject, new Dictionary<string, string> { ["token"] = "secret-token" });

        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData("secret-value\n")]
    [InlineData("secret\r\nvalue")]
    public void ApiKeyHeaderHandler_RejectsLineBreaksWithoutLeakingValue(string apiKey)
    {
        var handler = new ApiKeyHeaderAuthSchemeHandler();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://downstream.example");
        JsonElement config = JsonSerializer.Deserialize<JsonElement>("""{"header_name":"X-Api-Key"}""");

        var error = Assert.Throws<DeliveryConfigurationException>(
            () => handler.Apply(request, config, new Dictionary<string, string> { ["api_key"] = apiKey }));

        Assert.Equal(
            "Auth secret field 'api_key' contains a line break, which is not permitted in an HTTP header value.",
            error.Message);
        Assert.False(request.Headers.Contains("X-Api-Key"));
    }

    [Theory]
    [InlineData("secret-token\n")]
    [InlineData("secret\r\ntoken")]
    public void BearerTokenHandler_RejectsLineBreaksWithoutLeakingValue(string token)
    {
        var handler = new BearerTokenAuthSchemeHandler();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://downstream.example");

        var error = Assert.Throws<DeliveryConfigurationException>(
            () => handler.Apply(request, EmptyObject, new Dictionary<string, string> { ["token"] = token }));

        Assert.Equal(
            "Auth secret field 'token' contains a line break, which is not permitted in an HTTP header value.",
            error.Message);
        Assert.Null(request.Headers.Authorization);
    }

    private static IAuthSchemeRegistry CreateRegistry()
    {
        return new AuthSchemeRegistry(
        [
            new ApiKeyHeaderAuthSchemeHandler(),
            new BearerTokenAuthSchemeHandler()
        ]);
    }
}
