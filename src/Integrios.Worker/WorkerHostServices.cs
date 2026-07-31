using Integrios.Application.Delivery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Worker;

internal static class WorkerHostServices
{
    internal static IServiceCollection AddWorkerHostServices(
        this IServiceCollection services,
        IConfiguration configuration,
        bool enableBackgroundLoops)
    {
        services.AddOptions<HostOptions>()
            .Configure<DeliveryExecutionOptions>((hostOptions, deliveryOptions) =>
                hostOptions.ShutdownTimeout = deliveryOptions.ShutdownGracePeriod);

        if (!enableBackgroundLoops)
            return services;

        services.AddSingleton(FanoutLoopOptions.FromConfiguration(configuration));
        services.AddSingleton(DeliveryLoopOptions.FromConfiguration(configuration));
        services.AddSingleton<IWorkerLoopDelay, WorkerLoopDelay>();
        services.AddHostedService<OutboxFanoutWorker>();
        services.AddHostedService<SubscriptionDeliveryWorker>();

        return services;
    }
}
