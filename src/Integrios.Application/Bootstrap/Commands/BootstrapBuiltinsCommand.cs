using Integrios.Application.Connectors;
using Integrios.Application.Auth;
using Integrios.Domain.Connectors;
using MediatR;

namespace Integrios.Application.Bootstrap;

public sealed record BootstrapBuiltinsCommand : IRequest<IReadOnlyList<Connector>>;

internal sealed class BootstrapBuiltinsCommandHandler(
    IConnectorManifestStore manifestStore,
    IAuthSchemeRegistry authenticationSchemes,
    ISourceAdapterRegistry sourceAdapters)
    : IRequestHandler<BootstrapBuiltinsCommand, IReadOnlyList<Connector>>
{
    public async Task<IReadOnlyList<Connector>> Handle(BootstrapBuiltinsCommand command, CancellationToken cancellationToken)
    {
        var reconciled = new List<Connector>();
        foreach (BuiltinConnector builtin in BuiltinCatalog.All)
        {
            ConnectorManifestApplyAuthority authority =
                ConnectorManifestApplyAuthority.Bootstrap(builtin.Id);
            ConnectorManifest manifest = ConnectorManifestParser.Parse(
                ConnectorManifestParser.ToJson(builtin.Manifest),
                authenticationSchemes,
                sourceAdapters,
                authority);
            ConnectorManifestStoreResult result = await manifestStore.ApplyAsync(
                manifest,
                authority,
                cancellationToken);
            reconciled.Add(result.Connector);
        }

        return reconciled;
    }
}
