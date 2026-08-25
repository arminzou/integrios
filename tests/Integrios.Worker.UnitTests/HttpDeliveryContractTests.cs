using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Worker.UnitTests;

public sealed class HttpDeliveryContractTests
{
    [Fact]
    public void Configuration_SerializesTheExactVersionOneShape()
    {
        var configuration = new HttpDeliveryConfiguration
        {
            Version = 1,
            Method = "PATCH",
            Path = "contacts",
            Headers = new Dictionary<string, string> { ["X-Operation"] = "upsert" },
            Body = "json"
        };

        string json = JsonSerializer.Serialize(configuration, StoredJson.Options);

        json.ShouldBe(
            """{"version":1,"method":"PATCH","path":"contacts","headers":{"X-Operation":"upsert"},"body":"json"}""");
    }

    [Fact]
    public void Authoring_RejectsUnknownConfigurationVersion()
    {
        var configuration = Configuration() with { Version = 2 };

        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.Validate(configuration));

        error.Message.ShouldBe("http_delivery.version must be 1.");
    }

    [Theory]
    [InlineData("""{"version":1,"method":"POST","headers":{},"body":"json","unknown":true}""")]
    [InlineData("""{"version":1,"method":"POST","path_expression":{"engine":"jsonata","version":"1","expression":"id"},"headers":{},"body":"json"}""")]
    public void Configuration_RejectsUnknownMembers(string json)
    {
        Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<HttpDeliveryConfiguration>(json, StoredJson.Options));
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
        IDestinationAuthenticatorRegistry registry = new DestinationAuthenticatorRegistry([new ApiKeyHeaderAuthenticator()]);

        var error = Should.Throw<SubscriptionValidationException>(
            () => HttpDeliveryConfigurationRules.ValidateAuthenticationHeaderCollisions(
                configuration,
                selection,
                registry));

        error.Message.ShouldBe("http_delivery header 'X-API-KEY' is owned by destination authentication.");
    }

    [Theory]
    [InlineData("https://slack.com/api", "chat.postMessage", "https://slack.com/api/chat.postMessage")]
    [InlineData("https://slack.com/api/", "/chat.postMessage", "https://slack.com/api/chat.postMessage")]
    [InlineData("https://slack.com/api///", "/chat.postMessage", "https://slack.com/api/chat.postMessage")]
    [InlineData("https://dsg.crm.test/api/data/v9.2", "/contacts?$select=fullname,emailaddress1", "https://dsg.crm.test/api/data/v9.2/contacts?$select=fullname,emailaddress1")]
    [InlineData("https://dsg.crm.test/api/data/v9.2", "?$select=fullname", "https://dsg.crm.test/api/data/v9.2?$select=fullname")]
    public void Composition_AppendsToThePreservedBasePath(
        string baseUri,
        string relativeTarget,
        string expected)
    {
        HttpTargetComposer.Compose(baseUri, relativeTarget).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Composition_AbsentOrEmptyPathReturnsTheBaseUriUnchanged(string? relativeTarget)
    {
        const string baseUri = "https://legacy.example.test/exact-target";

        string result = HttpTargetComposer.Compose(baseUri, relativeTarget);

        result.ShouldBe(baseUri);
    }

    [Theory]
    [InlineData("https://attacker.test/path")]
    [InlineData("//attacker.test/path")]
    [InlineData("path#fragment")]
    [InlineData("../outside")]
    [InlineData("inside/../../outside")]
    [InlineData("%2e%2e/outside")]
    [InlineData("%252e%252e/outside")]
    [InlineData("inside%2f..%2foutside")]
    public void Composition_RejectsTargetsThatEscapeOrAreNotRequestTargets(string relativeTarget)
    {
        Should.Throw<DeliveryConfigurationException>(
            () => HttpTargetComposer.Compose("https://destination.test/base", relativeTarget));
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
