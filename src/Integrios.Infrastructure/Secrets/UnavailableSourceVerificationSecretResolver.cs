using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

internal sealed class UnavailableSourceVerificationSecretResolver : ISourceVerificationSecretResolver
{
    public string ProviderName => "unavailable";

    public Task<string> ResolveAsync(TenantSecretScope tenant, string secretReference, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Source-verification secret resolution is available only in the Ingress process.");
}
