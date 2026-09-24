using Azure.Identity;
using Integrios.Application.Secrets;
using Integrios.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Integrios.Infrastructure;

public static class SecretResolutionDependencyInjection
{
    private const string KeyPerFileDirectory = "/run/secrets/integrios";

    // Both sources load once: a new or rotated secret takes effect when the process restarts.
    // An unreachable or unauthorized vault throws here, so the process fails to start.
    public static IConfigurationManager AddSecretConfigurationSources(this IConfigurationManager configuration)
    {
        configuration.AddKeyPerFile(KeyPerFileDirectory, optional: true, reloadOnChange: false);

        string? keyVaultUri = configuration["Integrios:KeyVault:Uri"];
        if (!string.IsNullOrWhiteSpace(keyVaultUri))
            configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());

        return configuration;
    }

    public static IServiceCollection AddDestinationAuthenticationSecretResolutionServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Replace(ServiceDescriptor.Singleton<IDestinationAuthenticationSecretResolver>(
            new DestinationAuthenticationConfigurationSecretResolver(configuration)));
        return services;
    }

    public static IServiceCollection AddSourceVerificationSecretResolutionServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Replace(ServiceDescriptor.Singleton<ISourceVerificationSecretResolver>(
            new SourceVerificationConfigurationSecretResolver(configuration)));
        return services;
    }
}
