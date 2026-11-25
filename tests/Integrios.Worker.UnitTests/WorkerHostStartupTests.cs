using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Integrios.Worker.UnitTests;

public sealed class WorkerHostStartupTests
{
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
        hostedTypes.ShouldContain(typeof(OutboxFanoutWorker));
        hostedTypes.ShouldContain(typeof(EventDeliveryWorker));
        hostedTypes.Length.ShouldBe(2);
        provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.ShouldBe(
            provider.GetRequiredService<DeliveryExecutionOptions>().ShutdownGracePeriod);
    }

    [Fact]
    public void WorkerHost_ConfiguredRetentionRegistersIndependentCleanupLoop()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [HistoryRetentionOptions.ConfigurationKey] = "7.00:00:00"
        });
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
        hostedTypes.ShouldContain(typeof(OutboxFanoutWorker));
        hostedTypes.ShouldContain(typeof(EventDeliveryWorker));
        hostedTypes.ShouldContain(typeof(CompletedHistoryCleanupWorker));
        hostedTypes.Length.ShouldBe(3);
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

        services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IHostedService));
        services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(FanoutLoopOptions));
        services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(DeliveryLoopOptions));
        services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(HistoryRetentionOptions));
        provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.ShouldBe(
            provider.GetRequiredService<DeliveryExecutionOptions>().ShutdownGracePeriod);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        values ??= [];
        values["ConnectionStrings:Postgres"] =
            "Host=localhost;Database=integrios;Username=integrios;Password=integrios";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
