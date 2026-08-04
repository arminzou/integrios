using Integrios.Application.Integrations;
using Integrios.Application.Auth;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record BootstrapBuiltinsCommand : IRequest<IReadOnlyList<Integration>>;

internal sealed class BootstrapBuiltinsCommandHandler(
    IIntegrationManifestStore manifestStore,
    IAuthSchemeRegistry authenticationSchemes,
    ISourceAdapterRegistry sourceAdapters)
    : IRequestHandler<BootstrapBuiltinsCommand, IReadOnlyList<Integration>>
{
    public async Task<IReadOnlyList<Integration>> Handle(BootstrapBuiltinsCommand command, CancellationToken cancellationToken)
    {
        var reconciled = new List<Integration>();
        foreach (BuiltinIntegration builtin in BuiltinCatalog.All)
        {
            IntegrationManifestApplyAuthority authority =
                IntegrationManifestApplyAuthority.Bootstrap(builtin.Id);
            IntegrationManifest manifest = IntegrationManifestParser.Parse(
                IntegrationManifestParser.ToJson(builtin.Manifest),
                authenticationSchemes,
                sourceAdapters,
                authority);
            IntegrationManifestStoreResult result = await manifestStore.ApplyAsync(
                manifest,
                authority,
                cancellationToken);
            reconciled.Add(result.Integration);
        }

        return reconciled;
    }
}
