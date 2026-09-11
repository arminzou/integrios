using System.Text.Json;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Tests.Shared;

namespace Integrios.Application.UnitTests;

public sealed class DestinationUseValidatorTests
{
    private static readonly IDestinationAuthenticatorRegistry AuthenticationSchemes =
        new FakeDestinationAuthenticatorRegistry(new FakeBearerTokenAuthenticator(), new FakeApiKeyHeaderAuthenticator());

    [Fact]
    public void ActiveDestination_WithDeclaredAuthentication_IsValid()
    {
        Connector connector = ConnectorFor("destination", [Scheme("bearer_token", secrets: ["token"])]);
        Destination destination = DestinationFor(Json("""{"base_uri":"https://example.test/hook"}"""),
            new DestinationAuthentication
            {
                Scheme = "bearer_token",
                Config = Json("{}"),
                SecretRefs = Json("""{"token":"destination_token"}""")
            });

        DestinationUseValidator.ValidateAuthoring(destination, connector, AuthenticationSchemes);
    }

    [Fact]
    public void SourceOnlyConnector_IsRejected()
    {
        DestinationValidationException exception = Should.Throw<DestinationValidationException>(() =>
            DestinationUseValidator.ValidateAuthoring(
                DestinationFor(Json("""{"base_uri":"https://example.test/hook"}""")),
                ConnectorFor("source"),
                AuthenticationSchemes));

        exception.Message.ShouldContain("does not permit Destination authoring", Case.Sensitive);
    }

    [Theory]
    [InlineData("ftp://example.test/hook")]
    [InlineData("https://example.test/hook?query=value")]
    public void InvalidBaseUri_IsRejected(string baseUri)
    {
        DestinationValidationException exception = Should.Throw<DestinationValidationException>(() =>
            DestinationUseValidator.ValidateAuthoring(
                DestinationFor(Json($$"""{"base_uri":"{{baseUri}}"}""")),
                ConnectorFor("destination"),
                AuthenticationSchemes));

        exception.Message.ShouldContain("absolute HTTP or HTTPS", Case.Sensitive);
    }

    private static Connector ConnectorFor(string direction, IReadOnlyList<ConnectorSchemeManifest>? schemes = null)
    {
        var manifest = new ConnectorManifest
        {
            ManifestSchemaVersion = 1, Key = "provider", ContractVersion = 1, Direction = direction,
            DestinationConfigurationSchema = direction is "destination" or "both"
                ? Json("""{"type":"object","properties":{"base_uri":{"type":"string"}},"required":["base_uri"],"additionalProperties":true}""") : null,
            SourceVerification = new ConnectorSourceVerificationManifest { AllowUnverified = true },
            DestinationAuthentication = new ConnectorDestinationAuthenticationManifest
            { AllowUnauthenticated = schemes is not { Count: > 0 }, Schemes = schemes ?? [] },
            Presentation = new ConnectorPresentationManifest { Name = "Provider" },
        };
        return new Connector
        {
            Id = Guid.NewGuid(), Key = manifest.Key, ContractVersion = 1, ManifestSchemaVersion = 1,
            Name = "Provider", Direction = Enum.Parse<ConnectorDirection>(direction, true), Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Manifest = manifest,
        };
    }

    private static Destination DestinationFor(JsonElement configuration, DestinationAuthentication? authentication = null) => new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ConnectorId = Guid.NewGuid(), Name = "destination",
        Configuration = configuration, Authentication = authentication, Status = OperationalStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ConnectorSchemeManifest Scheme(string scheme, IReadOnlyList<string>? secrets = null) => new()
    { Scheme = scheme, RequiredSecretRefs = secrets ?? [] };

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
