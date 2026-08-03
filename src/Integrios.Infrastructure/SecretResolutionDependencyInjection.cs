using Integrios.Application.Secrets;
using Integrios.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Integrios.Infrastructure;

public static class SecretResolutionDependencyInjection
{
    public static IServiceCollection AddDestinationAuthenticationSecretResolutionServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string provider = configuration["Integrios:DestinationSecrets:Provider"]?.Trim().ToLowerInvariant() ?? "file";

        return provider switch
        {
            "file" => AddDestinationFileResolver(services, configuration),
            "configuration" => ReplaceResolver(
                services,
                new DestinationAuthenticationConfigurationSecretResolver(configuration)),
            _ => throw new InvalidOperationException($"Unsupported destination-secrets provider '{provider}'.")
        };
    }

    public static IServiceCollection AddSourceVerificationSecretResolutionServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string provider = configuration["Integrios:SourceSecrets:Provider"]?.Trim().ToLowerInvariant()
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
        string? configuredRoot = configuration["Integrios:SourceSecrets:FileRoot"];
        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? SourceVerificationMountedFileSecretResolver.DefaultRoot
            : configuredRoot;

        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Integrios:SourceSecrets:FileRoot must be an absolute path.");
        if (!Directory.Exists(root))
            throw new InvalidOperationException("Configured Integrios:SourceSecrets:FileRoot does not exist.");

        return new SourceVerificationMountedFileSecretResolver(root);
    }

    private static IServiceCollection AddDestinationFileResolver(
        IServiceCollection services,
        IConfiguration configuration)
    {
        string? configuredRoot = configuration["Integrios:DestinationSecrets:FileRoot"];
        string root = string.IsNullOrWhiteSpace(configuredRoot)
            ? DestinationAuthenticationMountedFileSecretResolver.DefaultRoot
            : configuredRoot;

        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Integrios:DestinationSecrets:FileRoot must be an absolute path.");

        if (!Directory.Exists(root))
            throw new InvalidOperationException("Configured Integrios:DestinationSecrets:FileRoot does not exist.");

        return ReplaceResolver(services, new DestinationAuthenticationMountedFileSecretResolver(root));
    }

    private static IServiceCollection ReplaceResolver(
        IServiceCollection services,
        IDestinationAuthenticationSecretResolver resolver)
    {
        services.Replace(ServiceDescriptor.Singleton(resolver));
        return services;
    }
}
