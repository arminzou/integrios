using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Connectors;

public sealed record ComposeConnectorManifestQuery(
    string Key,
    int ContractVersion,
    string? Name,
    string? Description,
    ConnectorDirection Direction) : IRequest<ComposeConnectorManifestResult>;

public sealed record ComposeConnectorManifestResult(JsonElement Manifest);

internal sealed class ComposeConnectorManifestQueryHandler(
    IConnectorManifestStore store,
    ISourceVerifierRegistry sourceVerificationSchemes,
    IDestinationAuthenticatorRegistry authenticationSchemes)
    : IRequestHandler<ComposeConnectorManifestQuery, ComposeConnectorManifestResult>
{
    private static readonly JsonElement SourceConfigurationSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = true,
    });

    private static readonly JsonElement DestinationConfigurationSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { base_uri = new { type = "string", format = "uri" } },
        required = new[] { "base_uri" },
        additionalProperties = false,
    });

    public async Task<ComposeConnectorManifestResult> Handle(
        ComposeConnectorManifestQuery query,
        CancellationToken cancellationToken)
    {
        Connector? latest = await store.GetLatestByKeyAsync(query.Key, cancellationToken);

        bool sourceCapable = query.Direction is ConnectorDirection.Source or ConnectorDirection.Both;
        bool destinationCapable = query.Direction is ConnectorDirection.Destination or ConnectorDirection.Both;
        bool previousSourceCapable = latest?.Direction is ConnectorDirection.Source or ConnectorDirection.Both;
        bool previousDestinationCapable = latest?.Direction is ConnectorDirection.Destination or ConnectorDirection.Both;
        var composed = new ConnectorManifest
        {
            ManifestSchemaVersion = 1,
            Key = query.Key,
            ContractVersion = query.ContractVersion,
            Direction = query.Direction.ToString().ToLowerInvariant(),
            SourceConfigurationSchema = sourceCapable
                ? latest?.Manifest.SourceConfigurationSchema ?? SourceConfigurationSchema
                : null,
            DestinationConfigurationSchema = destinationCapable
                ? latest?.Manifest.DestinationConfigurationSchema ?? DestinationConfigurationSchema
                : null,
            SourceVerification = new ConnectorSourceVerificationManifest
            {
                AllowUnverified = true,
                Schemes = sourceCapable
                    ? previousSourceCapable
                        ? latest!.Manifest.SourceVerification.Schemes
                        : sourceVerificationSchemes.Registered.Select(ToManifest).ToArray()
                    : [],
            },
            DestinationAuthentication = new ConnectorDestinationAuthenticationManifest
            {
                AllowUnauthenticated = true,
                Schemes = destinationCapable
                    ? previousDestinationCapable
                        ? latest!.Manifest.DestinationAuthentication.Schemes
                        : authenticationSchemes.Registered.Select(ToManifest).ToArray()
                    : [],
            },
            Presentation = new ConnectorPresentationManifest
            {
                Name = query.Name?.Trim() ?? "",
                Description = string.IsNullOrWhiteSpace(query.Description) ? null : query.Description.Trim(),
                EventTypes = latest?.Manifest.Presentation.EventTypes ?? [],
                AuthoringPresets = latest?.Manifest.Presentation.AuthoringPresets ?? [],
            },
        };

        ConnectorManifest parsed = ConnectorManifestParser.Parse(
            ConnectorManifestParser.ToJson(composed),
            sourceVerificationSchemes,
            authenticationSchemes);
        return new ComposeConnectorManifestResult(ConnectorManifestParser.ToJson(parsed));
    }

    private static ConnectorSchemeManifest ToManifest(ISourceVerifier verifier) => new()
    {
        Scheme = verifier.Scheme,
        RequiredConfig = verifier.RequiredConfigFields,
        RequiredSecretRefs = verifier.RequiredSecretFields,
    };

    private static ConnectorSchemeManifest ToManifest(IDestinationAuthenticator handler) => new()
    {
        Scheme = handler.Name,
        RequiredConfig = handler.RequiredConfigFields,
        RequiredSecretRefs = handler.RequiredSecretFields,
    };
}
