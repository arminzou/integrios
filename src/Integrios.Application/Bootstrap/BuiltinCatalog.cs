using Integrios.Domain.Integrations;

namespace Integrios.Application.Bootstrap;

public sealed record BuiltinIntegration(
    Guid Id,
    IntegrationManifest Manifest);

public static class BuiltinCatalog
{
    public static readonly Guid WebhookId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static readonly IReadOnlyList<BuiltinIntegration> All =
    [
        new BuiltinIntegration(
            WebhookId,
            new IntegrationManifest
            {
                ManifestSchemaVersion = 1,
                Key = "webhook",
                ContractVersion = 1,
                Direction = "both",
                SourceConfigurationSchema = EmptyObjectSchema(),
                DestinationConfigurationSchema = WebhookDestinationSchema(),
                SourceVerification = new IntegrationSourceVerificationManifest { AllowUnverified = true },
                DestinationAuthentication = new IntegrationDestinationAuthenticationManifest { AllowUnauthenticated = true },
                Presentation = new IntegrationPresentationManifest
                {
                    Name = "Webhook",
                    Description = "Generic webhook source or destination over HTTP.",
                },
            }),
    ];

    private static System.Text.Json.JsonElement EmptyObjectSchema() =>
        System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            """{"type":"object","properties":{},"additionalProperties":true}""");

    private static System.Text.Json.JsonElement WebhookDestinationSchema() =>
        System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            """{"type":"object","properties":{"url":{"type":"string","format":"uri"}},"required":["url"],"additionalProperties":true}""");
}
