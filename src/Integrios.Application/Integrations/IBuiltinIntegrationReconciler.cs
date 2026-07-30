using Integrios.Domain.Integrations;

namespace Integrios.Application.Integrations;

public interface IBuiltinIntegrationReconciler
{
    Task<Integration> ReconcileAsync(
        Integration integration,
        CancellationToken cancellationToken = default);
}
