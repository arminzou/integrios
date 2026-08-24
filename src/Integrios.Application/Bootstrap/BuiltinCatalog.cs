using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Bootstrap;

public sealed record BuiltinConnector(
    Guid Id,
    ConnectorManifest Manifest);

public static class BuiltinCatalog
{
    public static readonly Guid HttpId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static readonly IReadOnlyList<BuiltinConnector> All =
    [
        new BuiltinConnector(
            HttpId,
            new ConnectorManifest
            {
                ManifestSchemaVersion = 1,
                Key = "http",
                ContractVersion = 1,
                Direction = "both",
                SourceConfigurationSchema = EmptyObjectSchema(),
                DestinationConfigurationSchema = HttpDestinationSchema(),
                SourceVerification = new ConnectorSourceVerificationManifest { AllowUnverified = true },
                DestinationAuthentication = new ConnectorDestinationAuthenticationManifest
                {
                    AllowUnauthenticated = true,
                    Schemes =
                    [
                        new ConnectorSchemeManifest
                        {
                            Scheme = "api_key_header",
                            RequiredConfig = ["header_name"],
                            RequiredSecretRefs = ["api_key"],
                        },
                        new ConnectorSchemeManifest
                        {
                            Scheme = "bearer_token",
                            RequiredSecretRefs = ["token"],
                        },
                    ],
                },
                Presentation = new ConnectorPresentationManifest
                {
                    Name = "HTTP",
                    Description = "Generic HTTP source or destination.",
                },
            }),
    ];

    private static JsonElement EmptyObjectSchema() =>
        JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{},"additionalProperties":true}""");

    private static JsonElement HttpDestinationSchema() =>
        JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"base_uri":{"type":"string","format":"uri"}},"required":["base_uri"],"additionalProperties":false}""");
}
