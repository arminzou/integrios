using Integrios.Application.Abstractions.Auth;
using Microsoft.Extensions.Configuration;

namespace Integrios.Infrastructure.Http.Auth;

public sealed class ConfigurationSecretResolver(IConfiguration configuration) : ISecretResolver
{
    public string ProviderName => "configuration";

    public Task<string> ResolveAsync(
        TenantSecretScope tenant,
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecretValueValidator.ValidateScope(tenant, secretReference, ProviderName);

        string? value = configuration[$"Secrets:{tenant.Slug}:{secretReference}"];
        return Task.FromResult(SecretValueValidator.ValidateText(value, secretReference, ProviderName));
    }
}
