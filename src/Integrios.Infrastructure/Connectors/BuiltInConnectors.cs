using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Infrastructure.Connectors;

internal static class BuiltInConnectors
{
    private static readonly DateTimeOffset SeededAt = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    public static Connector Subscriber { get; } = new()
    {
        Id = Guid.Parse("5ab5c71b-e000-4000-8000-000000000001"),
        Key = Connector.SubscriberConnectorKey,
        ContractVersion = 1,
        ManifestSchemaVersion = 1,
        Name = "Subscriber",
        Description = "A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.",
        Direction = ConnectorDirection.Destination,
        CreatedAt = SeededAt,
        UpdatedAt = SeededAt,
        Manifest = new ConnectorManifest
        {
            ManifestSchemaVersion = 1,
            Key = Connector.SubscriberConnectorKey,
            ContractVersion = 1,
            Direction = "destination",
            DestinationConfigurationSchema = JsonDocument.Parse(
                """{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone(),
            SourceVerification = new ConnectorSourceVerificationManifest { AllowUnverified = true },
            DestinationAuthentication = new ConnectorDestinationAuthenticationManifest { AllowUnauthenticated = true },
            Presentation = new ConnectorPresentationManifest
            {
                Name = "Subscriber",
                Description = "A Subscriber service claims deliveries for this Destination and reports each outcome, instead of receiving HTTP requests.",
            },
        },
    };
}
