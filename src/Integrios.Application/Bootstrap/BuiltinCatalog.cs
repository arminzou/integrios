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
    public static readonly Guid GitHubId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public const int GitHubContractVersion = 1;
    public static readonly Guid DataverseId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    // Requires PrimaryEntityName (bounds the derived event type) and OperationId (the source
    // identity); global messages carry no primary entity and are rejected rather than silently
    // producing a malformed event type.
    // event_type derives from the (lower-cased) X-GitHub-Event header only; GitHub's own action
    // segment (e.g. issues.opened) is a documented, deliberately deferred enhancement, not required
    // by any current voe success criterion. source_event_id is the raw X-GitHub-Delivery header.
    public const string GitHubWebhookMapping =
        "{ \"event_type\": \"github.\" & $context.headers.`x-github-event`, "
        + "\"source_event_id\": $context.headers.`x-github-delivery`, \"payload\": $ }";

    public const string RemoteExecutionContextMapping =
        """
        $exists(PrimaryEntityName) and PrimaryEntityName != "" and $exists(OperationId)
          ? { "event_type": "dataverse." & PrimaryEntityName & "." & MessageName, "source_event_id": OperationId, "payload": $ }
          : $error("Dataverse remote_execution_context_json requires a non-global PrimaryEntityName and an OperationId.")
        """;

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
                SourceContracts =
                [
                    new ConnectorSourceContractManifest
                    {
                        Key = "event_json",
                        ContractVersion = 1,
                        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                    },
                ],
                Presentation = new ConnectorPresentationManifest
                {
                    Name = "HTTP",
                    Description = "Generic HTTP source or destination.",
                },
            }),
        new BuiltinConnector(
            GitHubId,
            new ConnectorManifest
            {
                ManifestSchemaVersion = 1,
                Key = "github",
                ContractVersion = GitHubContractVersion,
                Direction = "source",
                SourceConfigurationSchema = EmptyObjectSchema(),
                SourceVerification = new ConnectorSourceVerificationManifest
                {
                    AllowUnverified = false,
                    Schemes =
                    [
                        new ConnectorSchemeManifest
                        {
                            Scheme = "hmac_sha256",
                            RequiredSecretRefs = ["secret"],
                        },
                    ],
                },
                DestinationAuthentication = new ConnectorDestinationAuthenticationManifest { AllowUnauthenticated = true },
                SourceContracts =
                [
                    new ConnectorSourceContractManifest
                    {
                        Key = "github_webhook",
                        ContractVersion = GitHubContractVersion,
                        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                        Mapping = new ConnectorSourceMappingManifest
                        {
                            Engine = "jsonata",
                            Version = "1",
                            Expression = GitHubWebhookMapping,
                        },
                    },
                ],
                Presentation = new ConnectorPresentationManifest
                {
                    Name = "GitHub",
                    Description = "GitHub webhook source.",
                },
            }),
        new BuiltinConnector(
            DataverseId,
            new ConnectorManifest
            {
                ManifestSchemaVersion = 1,
                Key = "dataverse",
                ContractVersion = 1,
                Direction = "source",
                SourceConfigurationSchema = EmptyObjectSchema(),
                SourceVerification = new ConnectorSourceVerificationManifest { AllowUnverified = true },
                DestinationAuthentication = new ConnectorDestinationAuthenticationManifest { AllowUnauthenticated = true },
                SourceContracts =
                [
                    new ConnectorSourceContractManifest
                    {
                        Key = "remote_execution_context_json",
                        ContractVersion = 1,
                        Config = JsonSerializer.Deserialize<JsonElement>("{}"),
                        Mapping = new ConnectorSourceMappingManifest
                        {
                            Engine = "jsonata",
                            Version = "1",
                            Expression = RemoteExecutionContextMapping,
                        },
                    },
                ],
                Presentation = new ConnectorPresentationManifest
                {
                    Name = "Dataverse",
                    Description = "Dataverse RemoteExecutionContext queue source.",
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
