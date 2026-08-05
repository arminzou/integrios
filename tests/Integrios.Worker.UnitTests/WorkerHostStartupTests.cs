using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Integrios.Worker.UnitTests;

public sealed class WorkerHostStartupTests
{
    [Fact]
    public void WorkerBackgroundInstrumentation_IsOnlyRegisteredWhenExplicitlyAdded()
    {
        IConfiguration configuration = BuildConfiguration();
        var admin = new ServiceCollection();
        admin.AddAdminApplicationServices();
        admin.AddAdminInfrastructureServices(configuration);
        admin.AddTelemetryServices(configuration, "integrios-admin");

        var ingress = new ServiceCollection();
        ingress.AddIngressApplicationServices();
        ingress.AddIngressInfrastructureServices(configuration);
        ingress.AddTelemetryServices(configuration, "integrios-ingress");

        var worker = new ServiceCollection();
        worker.AddWorkerApplicationServices();
        worker.AddWorkerInfrastructureServices(configuration);
        worker.AddTelemetryServices(configuration, "integrios-worker");
        worker.AddOutboxDepthMetricsServices(configuration);

        Assert.DoesNotContain(admin, IsOutboxDepthMetricsRegistration);
        Assert.DoesNotContain(ingress, IsOutboxDepthMetricsRegistration);
        Assert.Single(worker, IsOutboxDepthMetricsRegistration);
    }

    [Fact]
    public void WorkerHost_NormalStartupRegistersBothIndependentLoops()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = BuildConfiguration();
        services.AddLogging();
        services.AddWorkerApplicationServices();
        services.AddWorkerInfrastructureServices(configuration);
        services.AddWorkerHostServices(configuration, enableBackgroundLoops: true);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Type[] hostedTypes = provider.GetServices<IHostedService>()
            .Select(service => service.GetType())
            .ToArray();
        Assert.Contains(typeof(OutboxFanoutWorker), hostedTypes);
        Assert.Contains(typeof(SubscriptionDeliveryWorker), hostedTypes);
        Assert.Equal(2, hostedTypes.Length);
        Assert.Equal(
            provider.GetRequiredService<DeliveryExecutionOptions>().ShutdownGracePeriod,
            provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public void WorkerHost_SecretValidationCommandRegistersNeitherLoop()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = BuildConfiguration();
        services.AddLogging();
        services.AddWorkerApplicationServices();
        services.AddWorkerInfrastructureServices(configuration);
        services.AddWorkerHostServices(configuration, enableBackgroundLoops: false);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(FanoutLoopOptions));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(DeliveryLoopOptions));
        Assert.Equal(
            provider.GetRequiredService<DeliveryExecutionOptions>().ShutdownGracePeriod,
            provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        values ??= [];
        values["ConnectionStrings:Postgres"] =
            "Host=localhost;Database=integrios;Username=integrios;Password=integrios";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static bool IsOutboxDepthMetricsRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService)
        && descriptor.ImplementationType?.Name == "OutboxDepthMetrics";
}
