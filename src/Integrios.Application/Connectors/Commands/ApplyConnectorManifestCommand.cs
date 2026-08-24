using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Connectors;

public sealed record ApplyConnectorManifestResult(
    ConnectorDto Connector,
    ConnectorManifestApplyOutcome Outcome);

public sealed record ApplyConnectorManifestCommand(
    string Key,
    int ContractVersion,
    JsonElement Document) : IRequest<ApplyConnectorManifestResult>;

internal sealed class ApplyConnectorManifestCommandHandler(
    IConnectorManifestStore store,
    IAuthSchemeRegistry authenticationSchemes,
    ITransformEvaluator mappingEvaluator)
    : IRequestHandler<ApplyConnectorManifestCommand, ApplyConnectorManifestResult>
{
    public async Task<ApplyConnectorManifestResult> Handle(
        ApplyConnectorManifestCommand command,
        CancellationToken cancellationToken)
    {
        ConnectorManifestApplyAuthority authority = ConnectorManifestApplyAuthority.Operator;
        ConnectorManifest manifest = ConnectorManifestParser.Parse(
            command.Document,
            authenticationSchemes,
            mappingEvaluator,
            authority);

        if (!string.Equals(command.Key, manifest.Key, StringComparison.Ordinal)
            || command.ContractVersion != manifest.ContractVersion)
        {
            throw new ConnectorManifestValidationException(
                "The manifest key and contract_version must match the route identity.");
        }

        ConnectorManifestStoreResult applied = await store.ApplyAsync(
            manifest,
            authority,
            cancellationToken);

        return new ApplyConnectorManifestResult(
            ConnectorDto.From(applied.Connector),
            applied.Outcome);
    }
}
