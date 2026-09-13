using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Tests.Shared;
using NSubstitute;

namespace Integrios.Application.UnitTests;

public sealed class ComposeConnectorManifestQueryTests
{
    [Theory]
    [InlineData(ConnectorDirection.Source, true, false, false)]
    [InlineData(ConnectorDirection.Destination, false, true, false)]
    [InlineData(ConnectorDirection.Both, true, true, false)]
    [InlineData(ConnectorDirection.Source, true, false, true)]
    [InlineData(ConnectorDirection.Destination, false, true, true)]
    [InlineData(ConnectorDirection.Both, true, true, true)]
    public async Task Compose_UsesEveryRegisteredSchemeAndProducesAParseableManifest(
        ConnectorDirection direction,
        bool sourceCapable,
        bool destinationCapable,
        bool carryForward)
    {
        var sourceRegistry = new FakeSourceVerifierRegistry(new FakeHmacSha256SourceVerifier());
        var destinationRegistry = new FakeDestinationAuthenticatorRegistry(
            new FakeApiKeyHeaderAuthenticator(),
            new FakeBearerTokenAuthenticator());
        IConnectorManifestStore store = Substitute.For<IConnectorManifestStore>();
        Connector? latest = carryForward ? Existing(sourceRegistry, destinationRegistry) : null;
        store.GetLatestByKeyAsync("example_api", Arg.Any<CancellationToken>()).Returns(latest);
        var handler = new ComposeConnectorManifestQueryHandler(store, sourceRegistry, destinationRegistry);

        ComposeConnectorManifestResult result = await handler.Handle(
            new ComposeConnectorManifestQuery("example_api", 1, " Example API ", " Description ", direction),
            CancellationToken.None);

        ConnectorManifest manifest = ConnectorManifestParser.Parse(
            result.Manifest,
            sourceRegistry,
            destinationRegistry);
        manifest.Presentation.Name.ShouldBe("Example API");
        manifest.Presentation.Description.ShouldBe("Description");
        manifest.SourceConfigurationSchema.HasValue.ShouldBe(sourceCapable);
        manifest.DestinationConfigurationSchema.HasValue.ShouldBe(destinationCapable);
        manifest.SourceVerification.AllowUnverified.ShouldBeTrue();
        manifest.DestinationAuthentication.AllowUnauthenticated.ShouldBeTrue();
        manifest.SourceVerification.Schemes.Select(scheme => scheme.Scheme)
            .ShouldBe(sourceCapable ? ["hmac_sha256"] : []);
        manifest.DestinationAuthentication.Schemes.Select(scheme => scheme.Scheme)
            .ShouldBe(destinationCapable
                ? carryForward ? ["bearer_token"] : ["api_key_header", "bearer_token"]
                : []);
        manifest.Presentation.EventTypes.ShouldBe(carryForward ? ["example.updated"] : []);
        manifest.Presentation.AuthoringPresets.Count.ShouldBe(carryForward ? 1 : 0);
        if (sourceCapable && carryForward)
            manifest.SourceConfigurationSchema!.Value.GetProperty("required")[0].GetString().ShouldBe("region");
        if (destinationCapable && carryForward)
            manifest.DestinationConfigurationSchema!.Value.GetProperty("required")[0].GetString().ShouldBe("base_uri");
    }

    private static Connector Existing(
        FakeSourceVerifierRegistry sourceRegistry,
        FakeDestinationAuthenticatorRegistry destinationRegistry)
    {
        JsonElement document = JsonSerializer.Deserialize<JsonElement>("""
            {
              "manifest_schema_version":1,
              "key":"example_api",
              "contract_version":2,
              "direction":"both",
              "source_configuration_schema":{"type":"object","properties":{"region":{"type":"string"}},"required":["region"],"additionalProperties":false},
              "destination_configuration_schema":{"type":"object","properties":{"base_uri":{"type":"string","format":"uri"},"operation":{"type":"string"}},"required":["base_uri","operation"],"additionalProperties":false},
              "source_verification":{"allow_unverified":false,"schemes":[{"scheme":"hmac_sha256","required_config":[],"required_secret_refs":["secret"]}]},
              "destination_authentication":{"allow_unauthenticated":false,"schemes":[{"scheme":"bearer_token","required_config":[],"required_secret_refs":["token"]}]},
              "presentation":{"name":"Previous","event_types":["example.updated"],"authoring_presets":[{"label":"Update"}]}
            }
            """);
        ConnectorManifest manifest = ConnectorManifestParser.Parse(document, sourceRegistry, destinationRegistry);
        return new Connector
        {
            Id = Guid.NewGuid(),
            Key = manifest.Key,
            ContractVersion = manifest.ContractVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            Name = manifest.Presentation.Name,
            Direction = ConnectorDirection.Both,
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Manifest = manifest,
        };
    }
}
