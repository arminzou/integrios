using Integrios.Application.Abstractions.Auth;

namespace Integrios.Infrastructure.Http.Auth;

internal sealed class UnavailableSecretResolver : ISecretResolver
{
    public string ProviderName => "unavailable";

    public Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Secret resolution is available only in the Worker process.");
}
