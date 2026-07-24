using Integrios.Application.Delivery;
using Integrios.Application.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegriosApplication(this IServiceCollection services)
    {
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            // Outermost behavior: wraps every handler in a span before any other pipeline step.
            configuration.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        });

        services.AddSingleton<RetryPolicy>();
        services.AddSingleton<DeliveryOutcomePolicy>();

        services.AddMetrics();
        services.AddSingleton<IntegriosMetrics>();

        return services;
    }
}
