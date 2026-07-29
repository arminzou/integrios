using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

internal sealed class UnavailableSecretResolver : ISecretResolver
{
    public string ProviderName => "unavailable";

    public Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Secret resolution is available only in the Worker process.");
}
