using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Integrations;

public sealed record ApplyIntegrationManifestResult(
    IntegrationDto Integration,
    IntegrationManifestApplyOutcome Outcome);

public sealed record ApplyIntegrationManifestCommand(
    string Key,
    int ContractVersion,
    JsonElement Document) : IRequest<ApplyIntegrationManifestResult>;

internal sealed class ApplyIntegrationManifestCommandHandler(
    IIntegrationManifestStore store,
    IAuthSchemeRegistry authenticationSchemes,
    ISourceAdapterRegistry sourceAdapters)
    : IRequestHandler<ApplyIntegrationManifestCommand, ApplyIntegrationManifestResult>
{
    public async Task<ApplyIntegrationManifestResult> Handle(
        ApplyIntegrationManifestCommand command,
        CancellationToken cancellationToken)
    {
        IntegrationManifestApplyAuthority authority = IntegrationManifestApplyAuthority.Operator;
        IntegrationManifest manifest = IntegrationManifestParser.Parse(
            command.Document,
            authenticationSchemes,
            sourceAdapters,
            authority);

        if (!string.Equals(command.Key, manifest.Key, StringComparison.Ordinal)
            || command.ContractVersion != manifest.ContractVersion)
        {
            throw new IntegrationManifestValidationException(
                "The manifest key and contract_version must match the route identity.");
        }

        IntegrationManifestStoreResult applied = await store.ApplyAsync(
            manifest,
            authority,
            cancellationToken);

        return new ApplyIntegrationManifestResult(
            IntegrationDto.From(applied.Integration),
            applied.Outcome);
    }
}
