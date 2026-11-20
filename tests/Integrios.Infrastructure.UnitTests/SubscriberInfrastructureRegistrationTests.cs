using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Infrastructure.UnitTests;

public sealed class SubscriberInfrastructureRegistrationTests
{
    [Fact]
    public void SubscriberRegistration_DefaultsTheAttemptCeilingToThirtyMinutes()
    {
        using ServiceProvider provider = BuildSubscriber([]);

        provider.GetRequiredService<DeliveryExecutionOptions>().MaxAttemptDuration
            .ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void SubscriberRegistration_AppliesConfiguredCeilingLeaseAndRetryPolicy()
    {
        using ServiceProvider provider = BuildSubscriber(new()
        {
            ["Integrios:Subscriber:MaxAttemptDuration"] = "00:10:00",
            ["Integrios:Delivery:LeaseDuration"] = "00:03:00",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "6"
        });

        DeliveryExecutionOptions options = provider.GetRequiredService<DeliveryExecutionOptions>();
        options.MaxAttemptDuration.ShouldBe(TimeSpan.FromMinutes(10));
        options.LeaseDuration.ShouldBe(TimeSpan.FromMinutes(3));
        provider.GetRequiredService<RetryPolicy>().MaxAttempts.ShouldBe(6);
    }

    [Theory]
    [InlineData("00:02:00")]
    [InlineData("00:01:00")]
    public void SubscriberRegistration_CeilingNotAboveTheLease_FailsStartupRegistration(string ceiling)
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["Integrios:Subscriber:MaxAttemptDuration"] = ceiling
        });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddSubscriberInfrastructureServices(configuration));

        exception.Message.ShouldContain("Integrios:Subscriber:MaxAttemptDuration", Case.Sensitive);
    }

    [Fact]
    public void SubscriberRegistration_NonTimeSpanCeiling_FailsStartupRegistration()
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["Integrios:Subscriber:MaxAttemptDuration"] = "half an hour"
        });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddSubscriberInfrastructureServices(configuration));

        exception.Message.ShouldContain("Integrios:Subscriber:MaxAttemptDuration", Case.Sensitive);
    }

    [Fact]
    public void WorkerRegistration_NeverReadsTheAttemptCeiling()
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["Integrios:Subscriber:MaxAttemptDuration"] = "not-a-timespan",
            ["Integrios:Delivery:LeaseDuration"] = "01:00:00",
            ["Integrios:Delivery:ShutdownGracePeriod"] = "00:01:00"
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkerApplicationServices();
        services.AddWorkerInfrastructureServices(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<DeliveryExecutionOptions>().LeaseDuration.ShouldBe(TimeSpan.FromHours(1));
    }

    private static ServiceProvider BuildSubscriber(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubscriberApplicationServices();
        services.AddSubscriberInfrastructureServices(BuildConfiguration(values));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=integrios;Password=integrios";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
