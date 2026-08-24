using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Tests.Shared;

public static class TestConnectorManifest
{
    public static string Create(
        string key,
        string name,
        string direction,
        IReadOnlyList<string>? authenticationSchemes = null,
        IReadOnlyList<string>? sourceVerificationSchemes = null,
        string? description = "test connector",
        bool? allowUnauthenticated = null,
        bool? allowUnverified = null,
        bool verifiedWebhookSourceContract = false,
        bool declarativeSourceContract = false,
        string? httpSuccessJson = null)
    {
        JsonElement emptySchema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{},"additionalProperties":true}""");
        JsonElement httpDestinationSchema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"base_uri":{"type":"string","format":"uri"}},"required":["base_uri"],"additionalProperties":false}""");
        var manifest = new ConnectorManifest
        {
            ManifestSchemaVersion = 1,
            Key = key,
            ContractVersion = 1,
            Direction = direction,
            SourceConfigurationSchema = direction is "source" or "both" ? emptySchema : null,
            DestinationConfigurationSchema = direction is "destination" or "both"
                ? key == "http" ? httpDestinationSchema : emptySchema
                : null,
            SourceVerification = new ConnectorSourceVerificationManifest
            {
                AllowUnverified = allowUnverified ?? sourceVerificationSchemes is not { Count: > 0 },
                Schemes = (sourceVerificationSchemes ?? [])
                    .Select(SourceVerificationScheme)
                    .ToArray(),
            },
            DestinationAuthentication = new ConnectorDestinationAuthenticationManifest
            {
                AllowUnauthenticated = allowUnauthenticated ?? authenticationSchemes is not { Count: > 0 },
                Schemes = (authenticationSchemes ?? [])
                    .Select(AuthenticationScheme)
                    .ToArray(),
            },
            SourceContracts = verifiedWebhookSourceContract
                ?
                [
                    new ConnectorSourceContractManifest
                    {
                        Key = "verified_webhook",
                        ContractVersion = 1,
                        Config = JsonSerializer.Deserialize<JsonElement>(
                            """
                            {
                              "signature_header": "X-Hub-Signature-256",
                              "signature_encoding": "hex",
                              "signature_prefix": "sha256=",
                              "delivery_id_header": "X-GitHub-Delivery",
                              "event_type_header": "X-GitHub-Event",
                              "event_type_action_field": "action"
                            }
                            """),
                    },
                ]
                : declarativeSourceContract
                ?
                [
                    new ConnectorSourceContractManifest
                    {
                        Key = "event_json",
                        ContractVersion = 1,
                        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                        Mapping = new ConnectorSourceMappingManifest
                        {
                            Engine = "jsonata",
                            Version = "1",
                            Expression = "{ \"event_type\": \"event\", \"payload\": $ }",
                        },
                    },
                ]
                : [],
            HttpSuccess = httpSuccessJson is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(httpSuccessJson),
            Presentation = new ConnectorPresentationManifest
            {
                Name = name,
                Description = description,
            },
        };
        return ConnectorManifestParser.ToJson(manifest).GetRawText();
    }

    private static ConnectorSchemeManifest AuthenticationScheme(string scheme) => scheme switch
    {
        "api_key_header" => new ConnectorSchemeManifest
        {
            Scheme = scheme,
            RequiredConfig = ["header_name"],
            RequiredSecretRefs = ["api_key"],
        },
        "bearer_token" => new ConnectorSchemeManifest
        {
            Scheme = scheme,
            RequiredSecretRefs = ["token"],
        },
        _ => new ConnectorSchemeManifest { Scheme = scheme },
    };

    private static ConnectorSchemeManifest SourceVerificationScheme(string scheme) => scheme switch
    {
        "github_hmac_sha256" => new ConnectorSchemeManifest
        {
            Scheme = scheme,
            RequiredSecretRefs = ["webhook_secret"],
        },
        "hmac_sha256" => new ConnectorSchemeManifest
        {
            Scheme = scheme,
            RequiredSecretRefs = ["secret"],
        },
        _ => new ConnectorSchemeManifest { Scheme = scheme },
    };
}
