using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

internal sealed class UnavailableDestinationAuthenticationSecretResolver
    : IDestinationAuthenticationSecretResolver
{
    public string ProviderName => "unavailable";

    public Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Destination-authentication secret resolution is available only in the Worker process.");
}
