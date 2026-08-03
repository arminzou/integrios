using Integrios.Application.Secrets;
using Integrios.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Integrios.Infrastructure;

public static class SecretResolutionDependencyInjection
{
    public static IServiceCollection AddSecretResolutionServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string provider = configuration["Integrios:Secrets:Provider"]?.Trim().ToLowerInvariant() ?? "file";

        return provider switch
        {
            "file" => AddFileResolver(services, configuration),
            "configuration" => ReplaceResolver(
                services,
                new ConfigurationSecretResolver(configuration)),
            _ => throw new InvalidOperationException($"Unsupported secrets provider '{provider}'.")
        };
    }

    public static IServiceCollection AddSourceVerificationSecretResolutionServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string provider = configuration["Integrios:SourceVerificationSecrets:Provider"]?.Trim().ToLowerInvariant()
            ?? "file";

        ISourceVerificationSecretResolver resolver = provider switch
        {
            "file" => CreateSourceFileResolver(configuration),
            "configuration" => new SourceVerificationConfigurationSecretResolver(configuration),
            _ => throw new InvalidOperationException($"Unsupported source-verification secrets provider '{provider}'.")
        };

        services.Replace(ServiceDescriptor.Singleton(resolver));
        return services;
    }

    private static ISourceVerificationSecretResolver CreateSourceFileResolver(IConfiguration configuration)
    {
        string? configuredRoot = configuration["Integrios:SourceVerificationSecrets:FileRoot"];
        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? SourceVerificationMountedFileSecretResolver.DefaultRoot
            : configuredRoot;

        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Integrios:SourceVerificationSecrets:FileRoot must be an absolute path.");
        if (!string.IsNullOrWhiteSpace(configuredRoot) && !Directory.Exists(root))
            throw new InvalidOperationException("Configured Integrios:SourceVerificationSecrets:FileRoot does not exist.");

        return new SourceVerificationMountedFileSecretResolver(root);
    }

    private static IServiceCollection AddFileResolver(
        IServiceCollection services,
        IConfiguration configuration)
    {
        string? configuredRoot = configuration["Integrios:Secrets:FileRoot"];
        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? MountedFileSecretResolver.DefaultRoot
            : configuredRoot;

        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Integrios:Secrets:FileRoot must be an absolute path.");

        if (!string.IsNullOrWhiteSpace(configuredRoot) && !Directory.Exists(root))
            throw new InvalidOperationException("Configured Integrios:Secrets:FileRoot does not exist.");

        return ReplaceResolver(services, new MountedFileSecretResolver(root));
    }

    private static IServiceCollection ReplaceResolver(
        IServiceCollection services,
        IDestinationAuthenticationSecretResolver resolver)
    {
        services.Replace(ServiceDescriptor.Singleton(resolver));
        return services;
    }
}
