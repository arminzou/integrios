using Integrios.Domain.Integrations;

namespace Integrios.Application.Integrations;

public enum IntegrationManifestApplyMode
{
    Operator,
    Bootstrap,
}

public sealed class IntegrationManifestApplyAuthority
{
    private IntegrationManifestApplyAuthority(
        IntegrationManifestApplyMode mode,
        Guid? requiredIntegrationId)
    {
        Mode = mode;
        RequiredIntegrationId = requiredIntegrationId;
    }

    public IntegrationManifestApplyMode Mode { get; }
    public Guid? RequiredIntegrationId { get; }

    public static IntegrationManifestApplyAuthority Operator { get; } =
        new(IntegrationManifestApplyMode.Operator, null);

    public static IntegrationManifestApplyAuthority Bootstrap(Guid requiredIntegrationId)
    {
        if (requiredIntegrationId == Guid.Empty)
            throw new ArgumentException("Bootstrap Integration identity cannot be empty.", nameof(requiredIntegrationId));

        return new IntegrationManifestApplyAuthority(
            IntegrationManifestApplyMode.Bootstrap,
            requiredIntegrationId);
    }
}

public enum IntegrationManifestApplyOutcome
{
    Created,
    Unchanged,
    PresentationReconciled,
}

public sealed record IntegrationManifestStoreResult(
    Integration Integration,
    IntegrationManifestApplyOutcome Outcome);

public interface IIntegrationManifestStore
{
    Task<Integration?> GetByVersionAsync(
        string key,
        int contractVersion,
        CancellationToken cancellationToken);

    Task<IntegrationManifestStoreResult> ApplyAsync(
        IntegrationManifest manifest,
        IntegrationManifestApplyAuthority authority,
        CancellationToken cancellationToken);
}
