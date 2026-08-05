using System.Text.Json;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;

namespace Integrios.AcceptanceTests;

internal static class TestIntegrationManifest
{
    public static string Create(string key, string name, string direction, params string[] authenticationSchemes)
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
            DestinationAuthentication = new IntegrationDestinationAuthenticationManifest
            {
                AllowUnauthenticated = authenticationSchemes.Length == 0,
                Schemes = authenticationSchemes.Select(AuthenticationScheme).ToArray(),
            },
            Presentation = new IntegrationPresentationManifest
            {
                Name = name,
                Description = "Qualification-only integration",
            },
        }).GetRawText();
    }

    private static IntegrationSchemeManifest AuthenticationScheme(string scheme) => scheme switch
    {
        "api_key_header" => new IntegrationSchemeManifest
        {
            Scheme = scheme,
            RequiredConfig = ["header_name"],
            RequiredSecretRefs = ["api_key"],
        },
        "bearer_token" => new IntegrationSchemeManifest
        {
            Scheme = scheme,
            RequiredSecretRefs = ["token"],
        },
        _ => new IntegrationSchemeManifest { Scheme = scheme },
    };
}
