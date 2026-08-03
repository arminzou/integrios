using System.Text.Json;
using Integrios.Application.Connections;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure.Auth;

namespace Integrios.Admin.Tests;

public sealed class ConnectionUseValidatorTests
{
    private static readonly AuthSchemeRegistry AuthenticationSchemes = new(
        [new BearerTokenAuthSchemeHandler(), new ApiKeyHeaderAuthSchemeHandler()]);

    [Fact]
    public void BothCapableConnection_CanBeReadyForBothUsesWithoutPersistedDirection()
    {
        Integration integration = IntegrationFor(
            "both",
            sourceSchemes: [Scheme("github_hmac_sha256", secrets: ["webhook_secret"])],
            destinationSchemes: [Scheme("bearer_token", secrets: ["token"])]);
        Connection connection = ConnectionFor(
            Json("""{"workspace":"acme","url":"https://example.test/hook"}"""),
            source: Selection("github_hmac_sha256", "webhook_secret", "github_webhook_secret"),
            destination: Selection("bearer_token", "token", "slack_token"));

        ConnectionUseValidator.ValidateSourceReadiness(connection, integration);
        ConnectionUseValidator.ValidateDestinationReadiness(connection, integration, AuthenticationSchemes);
    }

    [Fact]
    public void DestinationUse_RequiresDeclaredAuthenticationSelection()
    {
        Integration integration = IntegrationFor(
            "destination",
            destinationSchemes: [Scheme("bearer_token", secrets: ["token"])]);
        Connection connection = ConnectionFor(Json("""{"url":"https://example.test/hook"}"""));

        ConnectionRequestValidationException exception = Assert.Throws<ConnectionRequestValidationException>(
            () => ConnectionUseValidator.ValidateDestinationReadiness(connection, integration, AuthenticationSchemes));

        Assert.Contains("requires a destination authentication selection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceUse_ValidatesTheDirectionalConfigurationSchema()
    {
        Integration integration = IntegrationFor(
            "source",
            sourceSchema: Json("""{"type":"object","properties":{"repository":{"type":"string","minLength":1}},"required":["repository"],"additionalProperties":false}"""));
        Connection connection = ConnectionFor(Json("""{"unexpected":true}"""));

        ConnectionRequestValidationException exception = Assert.Throws<ConnectionRequestValidationException>(
            () => ConnectionUseValidator.ValidateSourceReadiness(connection, integration));

        Assert.Contains("field 'repository' is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeactivatedConnection_CanStillBeValidatedForReadiness()
    {
        Integration integration = IntegrationFor("destination");
        Connection connection = ConnectionFor(
            Json("""{"url":"https://example.test/hook"}"""),
            status: OperationalStatus.Disabled);

        ConnectionUseValidator.ValidateDestinationReadiness(connection, integration, AuthenticationSchemes);
    }

    [Fact]
    public void DeactivatedConnection_CannotEstablishNewUse()
    {
        Integration integration = IntegrationFor("destination");
        Connection connection = ConnectionFor(
            Json("""{"url":"https://example.test/hook"}"""),
            status: OperationalStatus.Disabled);

        ConnectionRequestValidationException exception = Assert.Throws<ConnectionRequestValidationException>(
            () => ConnectionUseValidator.ValidateDestinationAuthoring(connection, integration, AuthenticationSchemes));

        Assert.Contains("must be active", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DestinationUse_RejectsNonHttpBaseUriWithoutInspectingIntegrationKey()
    {
        Integration integration = IntegrationFor("destination");
        Connection connection = ConnectionFor(Json("""{"url":"ftp://example.test/hook"}"""));

        ConnectionRequestValidationException exception = Assert.Throws<ConnectionRequestValidationException>(
            () => ConnectionUseValidator.ValidateDestinationReadiness(connection, integration, AuthenticationSchemes));

        Assert.Contains("HTTP or HTTPS", exception.Message, StringComparison.Ordinal);
        Assert.NotEqual("webhook", integration.Key);
    }

    private static Integration IntegrationFor(
        string direction,
        JsonElement? sourceSchema = null,
        IReadOnlyList<IntegrationSchemeManifest>? sourceSchemes = null,
        IReadOnlyList<IntegrationSchemeManifest>? destinationSchemes = null)
    {
        JsonElement emptySchema = Json("""{"type":"object","properties":{},"additionalProperties":true}""");
        JsonElement destinationSchema = Json("""{"type":"object","properties":{"url":{"type":"string","format":"uri"}},"required":["url"],"additionalProperties":true}""");
        var manifest = new IntegrationManifest
        {
            ManifestSchemaVersion = 1,
            Key = "provider",
            ContractVersion = 1,
            Direction = direction,
            SourceConfigurationSchema = direction is "source" or "both" ? sourceSchema ?? emptySchema : null,
            DestinationConfigurationSchema = direction is "destination" or "both" ? destinationSchema : null,
            SourceVerification = new IntegrationSourceVerificationManifest
            {
                AllowUnverified = sourceSchemes is not { Count: > 0 },
                Schemes = sourceSchemes ?? [],
            },
            DestinationAuthentication = new IntegrationDestinationAuthenticationManifest
            {
                AllowUnauthenticated = destinationSchemes is not { Count: > 0 },
                Schemes = destinationSchemes ?? [],
            },
            Presentation = new IntegrationPresentationManifest { Name = "Provider" },
        };
        return new Integration
        {
            Id = Guid.NewGuid(),
            Key = manifest.Key,
            ContractVersion = 1,
            ManifestSchemaVersion = 1,
            Name = "Provider",
            Direction = Enum.Parse<IntegrationDirection>(direction, true),
            SupportedAuthSchemes = manifest.DestinationAuthentication.Schemes.Select(s => s.Scheme).ToArray(),
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Manifest = manifest,
        };
    }

    private static Connection ConnectionFor(
        JsonElement config,
        ConnectionSchemeSelection? source = null,
        ConnectionSchemeSelection? destination = null,
        OperationalStatus status = OperationalStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        IntegrationId = Guid.NewGuid(),
        Name = "connection",
        Config = config,
        SourceVerification = source,
        DestinationAuthentication = destination,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ConnectionSchemeSelection Selection(string scheme, string field, string reference) => new()
    {
        Scheme = scheme,
        Config = Json("{}"),
        SecretRefs = Json($$"""{"{{field}}":"{{reference}}"}"""),
    };

    private static IntegrationSchemeManifest Scheme(
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
