using Integrios.Application.ApiKeys;
using Integrios.Application.Bootstrap;
using Integrios.Application.Connections;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Integrations;
using Integrios.Application.Outbox;
using Integrios.Application.Secrets;
using Integrios.Application.Subscriptions;
using Integrios.Application.Telemetry;
using Integrios.Application.Tenants;
using Integrios.Application.Topics;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application;

public static class DependencyInjection
{
    internal static IServiceCollection AddIntegriosApplication(this IServiceCollection services)
        => AddIntegriosApplication(services, static _ => true);

    public static IServiceCollection AddIntegriosAdminApplication(this IServiceCollection services)
        => AddIntegriosApplication(services, type => IsInCapability(
            type,
            typeof(IApiKeyRepository),
            typeof(BootstrapBuiltinsCommand),
            typeof(IConnectionRepository),
            typeof(IIntegrationRepository),
            typeof(ISubscriptionRepository),
            typeof(ITenantRepository),
            typeof(ITopicRepository)));

    public static IServiceCollection AddIntegriosIngressApplication(this IServiceCollection services)
        => AddIntegriosApplication(
            services,
            type => IsInCapability(type, typeof(IEventRepository)));

    public static IServiceCollection AddIntegriosWorkerApplication(this IServiceCollection services)
        => AddIntegriosApplication(
            services,
            type => IsInCapability(
                type,
                typeof(ISubscriptionDeliveryQueue),
                typeof(IOutboxFanout),
                typeof(ISecretResolver)));

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

    private static bool IsInCapability(Type type, params Type[] capabilityAnchors) =>
        type.Namespace is string typeNamespace
        && capabilityAnchors.Any(anchor =>
            anchor.Namespace is string capabilityNamespace
            && (typeNamespace.Equals(capabilityNamespace, StringComparison.Ordinal)
                || typeNamespace.StartsWith(capabilityNamespace + ".", StringComparison.Ordinal)));

}
