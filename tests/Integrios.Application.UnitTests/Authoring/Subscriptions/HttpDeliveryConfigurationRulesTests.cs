using System.Text.Json;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Delivery;
using Integrios.Domain.ValueObjects;
using Integrios.Tests.Shared;

namespace Integrios.Application.UnitTests;

public sealed class HttpDeliveryConfigurationRulesTests
{
    [Fact]
    public void Authoring_RejectsUnknownConfigurationVersion()
    {
        var configuration = Configuration() with { Version = 2 };

        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.Validate(configuration));

        error.Message.ShouldBe("http_delivery.version must be 1.");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Authoring_AcceptsTheSelectedMethods(string method)
    {
        HttpDeliveryConfigurationRules.Validate(Configuration(method: method));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("post")]
    [InlineData("")]
    public void Authoring_RejectsMethodsOutsideTheSelectedSet(string method)
    {
        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.Validate(Configuration(method: method)));

        error.Message.ShouldBe("http_delivery.method must be POST, PUT, PATCH, or DELETE.");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("none")]
    public void Authoring_AcceptsJsonOrNoBody(string body)
    {
        HttpDeliveryConfigurationRules.Validate(Configuration(body: body));
    }

    [Theory]
    [InlineData("form")]
    [InlineData("binary")]
    [InlineData("")]
    public void Authoring_RejectsOtherBodyModes(string body)
    {
        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.Validate(Configuration(body: body)));

        error.Message.ShouldBe("http_delivery.body must be 'json' or 'none'.");
    }

    [Fact]
    public void Authoring_AcceptsRestrictedStaticHeaders()
    {
        var configuration = Configuration(headers: new Dictionary<string, string>
        {
            ["X-Operation"] = "upsert",
            ["Accept-Language"] = "en-US"
        });

        HttpDeliveryConfigurationRules.Validate(configuration);
    }

    [Fact]
    public void Authoring_RejectsNullStaticHeaderValueAsValidationError()
    {
        var configuration = Configuration(headers: new Dictionary<string, string>
        {
            ["X-Operation"] = null!
        });

        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.Validate(configuration));

        error.Message.ShouldBe("http_delivery header 'X-Operation' value must be a string.");
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("content-type")]
    [InlineData("AUTHORIZATION")]
    [InlineData("integrios-delivery-id")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Content-Language")]
    [InlineData("content-encoding")]
    public void Authoring_RejectsReservedHeadersCaseInsensitively(string name)
    {
        var configuration = Configuration(headers: new Dictionary<string, string> { [name] = "value" });

        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.Validate(configuration));

        error.Message.ShouldContain("is reserved and cannot be configured", Case.Sensitive);
    }

    [Fact]
    public void Authoring_RejectsSelectedAuthenticationOwnedHeaderCaseInsensitively()
    {
        var configuration = Configuration(headers: new Dictionary<string, string> { ["x-api-key"] = "static" });
        DestinationAuthentication selection = Selection(
            "api_key_header",
            """{"header_name":"X-API-KEY"}""",
            """{"api_key":"destination_key"}""");
        IDestinationAuthenticatorRegistry registry = new FakeDestinationAuthenticatorRegistry(new FakeApiKeyHeaderAuthenticator());

        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.ValidateAuthenticationHeaderCollisions(
                configuration,
                selection,
                registry));

        error.Message.ShouldBe("http_delivery header 'X-API-KEY' is owned by destination authentication.");
    }

    private static HttpDeliveryConfiguration Configuration(
        string method = "POST",
        string? path = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string body = "json") => new()
        {
            Version = HttpDeliveryConfiguration.CurrentVersion,
            Method = method,
            Path = path,
            Headers = headers ?? new Dictionary<string, string>(),
            Body = body
        };

    private static DestinationAuthentication Selection(string scheme, string config, string secretRefs) => new()
    {
        Scheme = scheme,
        Config = JsonSerializer.Deserialize<JsonElement>(config),
        SecretRefs = JsonSerializer.Deserialize<JsonElement>(secretRefs)
    };
}
