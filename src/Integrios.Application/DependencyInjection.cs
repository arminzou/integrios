using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Integrios.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegriosApplication(this IServiceCollection services)
        => AddIntegriosApplication(services, static _ => true);

    public static IServiceCollection AddIntegriosAdminApplication(this IServiceCollection services)
        => AddIntegriosApplication(services, type => IsInCapability(
            type,
            "Integrios.Application.ApiKeys",
            "Integrios.Application.Bootstrap",
            "Integrios.Application.Connections",
            "Integrios.Application.Integrations",
            "Integrios.Application.Subscriptions",
            "Integrios.Application.Tenants",
            "Integrios.Application.Topics"));

    public static IServiceCollection AddIntegriosIngressApplication(this IServiceCollection services)
        => AddIntegriosApplication(
            services,
            type => IsInCapability(type, "Integrios.Application.Events"));

    public static IServiceCollection AddIntegriosWorkerApplication(this IServiceCollection services)
        => AddIntegriosApplication(
            services,
            type => IsInCapability(
                type,
                "Integrios.Application.Delivery",
                "Integrios.Application.Outbox",
                "Integrios.Application.Secrets"));

    private static IServiceCollection AddIntegriosApplication(
        IServiceCollection services,
        Func<Type, bool> handlerFilter)
    {
        services.AddMediatR(configuration =>
        {
            configuration.TypeEvaluator = handlerFilter;
            configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            // Outermost behavior: wraps every handler in a span before any other pipeline step.
            configuration.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        });

        services.AddMetrics();
        services.AddSingleton<IntegriosMetrics>();

        return services;
    }

    private static bool IsInCapability(Type type, params string[] namespaces) =>
        type.Namespace is string typeNamespace
        && namespaces.Any(namespaceName =>
            typeNamespace.Equals(namespaceName, StringComparison.Ordinal)
            || typeNamespace.StartsWith(namespaceName + ".", StringComparison.Ordinal));

    public static IServiceCollection AddIntegriosDeliveryPolicies(this IServiceCollection services)
    {
        services.TryAddSingleton(DeliveryExecutionOptions.Default);
        services.TryAddSingleton<RetryPolicy>();
        services.TryAddSingleton<DeliveryOutcomePolicy>();

        return services;
    }
}
