using System.Text.Json;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Bootstrap;

public sealed record BuiltinIntegration(
    Guid Id,
    IntegrationManifest Manifest);

public static class BuiltinCatalog
{
    public static readonly Guid HttpId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static readonly IReadOnlyList<BuiltinIntegration> All =
    [
        new BuiltinIntegration(
            HttpId,
            new IntegrationManifest
            {
                ManifestSchemaVersion = 1,
                Key = "http",
                ContractVersion = 1,
                Direction = "both",
                SourceConfigurationSchema = EmptyObjectSchema(),
                DestinationConfigurationSchema = HttpDestinationSchema(),
                SourceVerification = new IntegrationSourceVerificationManifest { AllowUnverified = true },
                DestinationAuthentication = new IntegrationDestinationAuthenticationManifest
                {
                    AllowUnauthenticated = true,
                    Schemes =
                    [
                        new IntegrationSchemeManifest
                        {
                            Scheme = "api_key_header",
                            RequiredConfig = ["header_name"],
                            RequiredSecretRefs = ["api_key"],
                        },
                        new IntegrationSchemeManifest
                        {
                            Scheme = "bearer_token",
                            RequiredSecretRefs = ["token"],
                        },
                    ],
                },
                Presentation = new IntegrationPresentationManifest
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
