using System.Text.Json;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;

namespace Integrios.Infrastructure.IntegrationTests;

internal static class TestIntegrationManifest
{
    public static string Create(string key, string name, string direction)
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{},"additionalProperties":true}""");
        return IntegrationManifestParser.ToJson(new IntegrationManifest
        {
            ManifestSchemaVersion = 1,
            Key = key,
            ContractVersion = 1,
            Direction = direction,
            SourceConfigurationSchema = direction is "source" or "both" ? schema : null,
            DestinationConfigurationSchema = direction is "destination" or "both" ? schema : null,
            SourceVerification = new IntegrationSourceVerificationManifest { AllowUnverified = true },
            DestinationAuthentication = new IntegrationDestinationAuthenticationManifest { AllowUnauthenticated = true },
            Presentation = new IntegrationPresentationManifest { Name = name },
        }).GetRawText();
    }
}
