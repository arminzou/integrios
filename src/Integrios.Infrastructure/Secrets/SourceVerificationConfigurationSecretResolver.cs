using Integrios.Application.Secrets;
using Microsoft.Extensions.Configuration;

namespace Integrios.Infrastructure.Secrets;

internal sealed class SourceVerificationConfigurationSecretResolver(IConfiguration configuration)
    : ISourceVerificationSecretResolver
{
    public string ProviderName => "configuration";

    public Task<string> ResolveAsync(TenantSecretScope tenant, string secretReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecretValueValidator.ValidateScope(tenant, secretReference, ProviderName);
        string? value = configuration[$"SourceSecrets:{tenant.Slug}:{secretReference}"];
        return Task.FromResult(SecretValueValidator.ValidateText(value, secretReference, ProviderName));
    }
}
