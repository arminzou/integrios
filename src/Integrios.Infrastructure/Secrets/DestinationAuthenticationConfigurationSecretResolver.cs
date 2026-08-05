using Integrios.Application.Secrets;
using Microsoft.Extensions.Configuration;

namespace Integrios.Infrastructure.Secrets;

internal sealed class DestinationAuthenticationConfigurationSecretResolver(IConfiguration configuration)
    : IDestinationAuthenticationSecretResolver
{
    public string ProviderName => "configuration";

    public Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecretValueValidator.ValidateScope(tenant, secretReference, ProviderName);

        string? value = configuration[$"DestinationSecrets:{tenant.Slug}:{secretReference}"];
        return Task.FromResult(SecretValueValidator.ValidateText(value, secretReference, ProviderName));
    }
}
