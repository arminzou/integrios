using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connectors;

public enum ConnectorManifestApplyOutcome
{
    Created,
    Unchanged,
    PresentationReconciled,
}

public sealed record ConnectorManifestStoreResult(
    Connector Connector,
    ConnectorManifestApplyOutcome Outcome);

public interface IConnectorManifestStore
{
    Task<Connector?> GetByVersionAsync(
        string key,
        int contractVersion,
        CancellationToken cancellationToken);

    Task<ConnectorManifestStoreResult> ApplyAsync(
        ConnectorManifest manifest,
        CancellationToken cancellationToken);
}
