using Integrios.Application.ApiKeys;
using Integrios.Application.Bootstrap;
using Integrios.Application.Connections;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Connectors;
using Integrios.Application.Outbox;
using Integrios.Application.Recovery;
using Integrios.Application.Secrets;
using Integrios.Application.Subscriptions;
using Integrios.Application.Sources;
using Integrios.Application.Telemetry;
using Integrios.Application.Tenants;
using Integrios.Application.Topics;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application;

public static class DependencyInjection
{
    internal static IServiceCollection AddApplicationServices(this IServiceCollection services)
        => AddApplicationServices(services, static _ => true);

    public static IServiceCollection AddAdminApplicationServices(this IServiceCollection services)
        => AddApplicationServices(services, type => IsInCapability(
            type,
            typeof(IApiKeyRepository),
            typeof(BootstrapBuiltinsCommand),
            typeof(IConnectionRepository),
            typeof(IConnectorCatalog),
            typeof(ISubscriptionRepository),
            typeof(ISourceRepository),
            typeof(ITenantRepository),
            typeof(ITopicRepository),
            typeof(ReplaySubscriptionDeliveryCommand)));

    public static IServiceCollection AddIngressApplicationServices(this IServiceCollection services)
        => AddApplicationServices(
            services,
            type => IsInCapability(type, typeof(IEventAcceptance)));

    public static IServiceCollection AddWorkerApplicationServices(this IServiceCollection services)
        => AddApplicationServices(
            services,
            type => IsInCapability(
                type,
                typeof(ISubscriptionDeliveryQueue),
                typeof(IOutboxFanout),
                typeof(IDestinationAuthenticationSecretResolver)));

    private static IServiceCollection AddApplicationServices(
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
