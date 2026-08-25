using System.Text.Json;
using Integrios.Application.Authoring.Connections;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class ConnectionUseValidatorTests
{
    private static readonly DestinationAuthenticatorRegistry AuthenticationSchemes = new(
        [new BearerTokenAuthenticator(), new ApiKeyHeaderAuthenticator()]);

    [Fact]
    public void BothCapableConnection_CanBeReadyForBothUsesWithoutPersistedDirection()
    {
        Connector connector = ConnectorFor(
            "both",
            sourceSchemes: [Scheme("github_hmac_sha256", secrets: ["webhook_secret"])],
            destinationSchemes: [Scheme("bearer_token", secrets: ["token"])]);
        Connection connection = ConnectionFor(
            Json("""{"workspace":"acme","base_uri":"https://example.test/hook"}"""),
            source: SourceSelection("github_hmac_sha256", "webhook_secret", "github_webhook_secret"),
            destination: DestinationSelection("bearer_token", "token", "slack_token"));

        ConnectionUseValidator.ValidateSourceReadiness(connection, connector);
        ConnectionUseValidator.ValidateDestinationReadiness(connection, connector, AuthenticationSchemes);
    }

    [Fact]
    public void DestinationUse_RequiresDeclaredAuthenticationSelection()
    {
        Connector connector = ConnectorFor(
            "destination",
            destinationSchemes: [Scheme("bearer_token", secrets: ["token"])]);
        Connection connection = ConnectionFor(Json("""{"base_uri":"https://example.test/hook"}"""));

        ConnectionValidationException exception = Should.Throw<ConnectionValidationException>(
            () => ConnectionUseValidator.ValidateDestinationReadiness(connection, connector, AuthenticationSchemes));

        exception.Message.ShouldContain("requires a destination authentication selection", Case.Sensitive);
    }

    [Fact]
    public void SourceUse_ValidatesTheDirectionalConfigurationSchema()
    {
        Connector connector = ConnectorFor(
            "source",
            sourceSchema: Json("""{"type":"object","properties":{"repository":{"type":"string","minLength":1}},"required":["repository"],"additionalProperties":false}"""));
        Connection connection = ConnectionFor(Json("""{"unexpected":true}"""));

        ConnectionValidationException exception = Should.Throw<ConnectionValidationException>(
            () => ConnectionUseValidator.ValidateSourceReadiness(connection, connector));

        exception.Message.ShouldContain("field 'repository' is required", Case.Sensitive);
    }

    [Fact]
    public void DeactivatedConnection_CanStillBeValidatedForReadiness()
    {
        Connector connector = ConnectorFor("destination");
        Connection connection = ConnectionFor(
            Json("""{"base_uri":"https://example.test/hook"}"""),
            status: OperationalStatus.Disabled);

        ConnectionUseValidator.ValidateDestinationReadiness(connection, connector, AuthenticationSchemes);
    }

    [Fact]
    public void DeactivatedConnection_CannotEstablishNewUse()
    {
        Connector connector = ConnectorFor("destination");
        Connection connection = ConnectionFor(
            Json("""{"base_uri":"https://example.test/hook"}"""),
            status: OperationalStatus.Disabled);

        ConnectionValidationException exception = Should.Throw<ConnectionValidationException>(
            () => ConnectionUseValidator.ValidateDestinationAuthoring(connection, connector, AuthenticationSchemes));

        exception.Message.ShouldContain("must be active", Case.Sensitive);
    }

    [Fact]
    public void DestinationUse_RejectsNonHttpBaseUriWithoutInspectingConnectorKey()
    {
        Connector connector = ConnectorFor("destination");
        Connection connection = ConnectionFor(Json("""{"base_uri":"ftp://example.test/hook"}"""));

        ConnectionValidationException exception = Should.Throw<ConnectionValidationException>(
            () => ConnectionUseValidator.ValidateDestinationReadiness(connection, connector, AuthenticationSchemes));

        exception.Message.ShouldContain("HTTP or HTTPS", Case.Sensitive);
        connector.Key.ShouldNotBe("http");
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("Content-Type")]
    [InlineData("content-language")]
    [InlineData("Host")]
    [InlineData("Connection")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Trailer")]
    [InlineData("Integrios-Event-Id")]
    public void DestinationAuthenticationAuthoring_RejectsInvalidOrOwnedHeaderNames(string headerName)
    {
        Connector connector = ConnectorFor(
            "destination",
            destinationSchemes: [Scheme("api_key_header", config: ["header_name"], secrets: ["api_key"])]);
        var input = new DestinationAuthenticationInput
        {
            Scheme = "api_key_header",
            Config = Json($$"""{"header_name":"{{headerName}}"}"""),
            SecretRefs = Json("""{"api_key":"destination_key"}""")
        };

        Should.Throw<ConnectionValidationException>(
            () => ConnectionSchemeValidator.ValidateDestination(connector, input, AuthenticationSchemes));
    }

    [Fact]
    public void DestinationAuthenticationAuthoring_AllowsAuthenticationToOwnAuthorization()
    {
        Connector connector = ConnectorFor(
            "destination",
            destinationSchemes: [Scheme("api_key_header", config: ["header_name"], secrets: ["api_key"])]);
        var input = new DestinationAuthenticationInput
        {
            Scheme = "api_key_header",
            Config = Json("""{"header_name":"Authorization"}"""),
            SecretRefs = Json("""{"api_key":"destination_key"}""")
        };

        DestinationAuthentication? selection = ConnectionSchemeValidator.ValidateDestination(
            connector,
            input,
            AuthenticationSchemes);

        selection!.Config.GetProperty("header_name").GetString().ShouldBe("Authorization");
    }

    private static Connector ConnectorFor(
        string direction,
        JsonElement? sourceSchema = null,
        IReadOnlyList<ConnectorSchemeManifest>? sourceSchemes = null,
        IReadOnlyList<ConnectorSchemeManifest>? destinationSchemes = null)
    {
        JsonElement emptySchema = Json("""{"type":"object","properties":{},"additionalProperties":true}""");
        JsonElement destinationSchema = Json("""{"type":"object","properties":{"base_uri":{"type":"string","format":"uri"}},"required":["base_uri"],"additionalProperties":true}""");
        var manifest = new ConnectorManifest
        {
            ManifestSchemaVersion = 1,
            Key = "provider",
            ContractVersion = 1,
            Direction = direction,
            SourceConfigurationSchema = direction is "source" or "both" ? sourceSchema ?? emptySchema : null,
            DestinationConfigurationSchema = direction is "destination" or "both" ? destinationSchema : null,
            SourceVerification = new ConnectorSourceVerificationManifest
            {
                AllowUnverified = sourceSchemes is not { Count: > 0 },
                Schemes = sourceSchemes ?? [],
            },
            DestinationAuthentication = new ConnectorDestinationAuthenticationManifest
            {
                AllowUnauthenticated = destinationSchemes is not { Count: > 0 },
                Schemes = destinationSchemes ?? [],
            },
            Presentation = new ConnectorPresentationManifest { Name = "Provider" },
        };
        return new Connector
        {
            Id = Guid.NewGuid(),
            Key = manifest.Key,
            ContractVersion = 1,
            ManifestSchemaVersion = 1,
            Name = "Provider",
            Direction = Enum.Parse<ConnectorDirection>(direction, true),
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Manifest = manifest,
        };
    }

    private static Connection ConnectionFor(
        JsonElement config,
        SourceVerification? source = null,
        DestinationAuthentication? destination = null,
        OperationalStatus status = OperationalStatus.Active) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ConnectorId = Guid.NewGuid(),
            Name = "connection",
            Config = config,
            SourceVerification = source,
            DestinationAuthentication = destination,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static SourceVerification SourceSelection(string scheme, string field, string reference) => new()
    {
        Scheme = scheme,
        Config = Json("{}"),
        SecretRefs = Json($$"""{"{{field}}":"{{reference}}"}"""),
    };

    private static DestinationAuthentication DestinationSelection(string scheme, string field, string reference) => new()
    {
        Scheme = scheme,
        Config = Json("{}"),
        SecretRefs = Json($$"""{"{{field}}":"{{reference}}"}"""),
    };

    private static ConnectorSchemeManifest Scheme(
        string scheme,
        IReadOnlyList<string>? config = null,
        IReadOnlyList<string>? secrets = null) => new()
        {
            Scheme = scheme,
            RequiredConfig = config ?? [],
            RequiredSecretRefs = secrets ?? [],
        };

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
