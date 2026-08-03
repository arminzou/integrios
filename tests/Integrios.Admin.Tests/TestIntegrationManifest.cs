using System.Text.Json;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;

namespace Integrios.Admin.Tests;

internal static class TestIntegrationManifest
{
    public static string Create(
        string key,
        string name,
        string direction,
        IReadOnlyList<string>? authenticationSchemes = null,
        IReadOnlyList<string>? sourceVerificationSchemes = null,
        string? description = "test integration")
    {
        JsonElement emptySchema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{},"additionalProperties":true}""");
        var manifest = new IntegrationManifest
        {
            ManifestSchemaVersion = 1,
            Key = key,
            ContractVersion = 1,
            Direction = direction,
            SourceConfigurationSchema = direction is "source" or "both" ? emptySchema : null,
            DestinationConfigurationSchema = direction is "destination" or "both" ? emptySchema : null,
            SourceVerificationSchemes = (sourceVerificationSchemes ?? [])
                .Select(SourceVerificationScheme)
                .ToArray(),
            DestinationAuthenticationSchemes = (authenticationSchemes ?? [])
                .Select(AuthenticationScheme)
                .ToArray(),
            Presentation = new IntegrationPresentationManifest
            {
                Name = name,
                Description = description,
            },
        };
        return IntegrationManifestParser.ToJson(manifest).GetRawText();
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

    private static IntegrationSchemeManifest SourceVerificationScheme(string scheme) => scheme switch
    {
        "github_hmac_sha256" => new IntegrationSchemeManifest
        {
            Scheme = scheme,
            RequiredSecretRefs = ["webhook_secret"],
        },
        _ => new IntegrationSchemeManifest { Scheme = scheme },
    };
}
