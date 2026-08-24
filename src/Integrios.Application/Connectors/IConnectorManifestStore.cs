using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Connectors;

public enum ConnectorManifestApplyMode
{
    Operator,
    Bootstrap,
}

public sealed class ConnectorManifestApplyAuthority
{
    private ConnectorManifestApplyAuthority(
        ConnectorManifestApplyMode mode,
        Guid? requiredConnectorId)
    {
        Mode = mode;
        RequiredConnectorId = requiredConnectorId;
    }

    public ConnectorManifestApplyMode Mode { get; }
    public Guid? RequiredConnectorId { get; }

    public static ConnectorManifestApplyAuthority Operator { get; } =
        new(ConnectorManifestApplyMode.Operator, null);

    public static ConnectorManifestApplyAuthority Bootstrap(Guid requiredConnectorId)
    {
        if (requiredConnectorId == Guid.Empty)
            throw new ArgumentException("Bootstrap Connector identity cannot be empty.", nameof(requiredConnectorId));

        return new ConnectorManifestApplyAuthority(
            ConnectorManifestApplyMode.Bootstrap,
            requiredConnectorId);
    }
}

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
        ConnectorManifestApplyAuthority authority,
        CancellationToken cancellationToken);
}
