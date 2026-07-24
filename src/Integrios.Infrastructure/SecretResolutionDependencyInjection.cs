using Integrios.Application.Abstractions.Auth;
using Integrios.Infrastructure.Http.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Integrios.Infrastructure;

public static class SecretResolutionDependencyInjection
{
    public static IServiceCollection AddIntegriosSecretResolution(
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

    private static IServiceCollection ReplaceResolver(IServiceCollection services, ISecretResolver resolver)
    {
        services.Replace(ServiceDescriptor.Singleton(resolver));
        return services;
    }
}
