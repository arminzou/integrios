using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Infrastructure.UnitTests;

public sealed class DeliveryApiInfrastructureRegistrationTests
{
    [Fact]
    public void DeliveryApiRegistration_DefaultsTheAttemptCeilingToThirtyMinutes()
    {
        using ServiceProvider provider = BuildDeliveryApi([]);

        provider.GetRequiredService<DeliveryExecutionOptions>().MaxAttemptDuration
            .ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void DeliveryApiRegistration_AppliesConfiguredCeilingLeaseAndRetryPolicy()
    {
        using ServiceProvider provider = BuildDeliveryApi(new()
        {
            ["Integrios:DeliveryApi:MaxAttemptDuration"] = "00:10:00",
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
    public void DeliveryApiRegistration_CeilingNotAboveTheLease_FailsStartupRegistration(string ceiling)
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["Integrios:DeliveryApi:MaxAttemptDuration"] = ceiling
        });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddDeliveryApiInfrastructureServices(configuration));

        exception.Message.ShouldContain("Integrios:DeliveryApi:MaxAttemptDuration", Case.Sensitive);
    }

    [Fact]
    public void DeliveryApiRegistration_NonTimeSpanCeiling_FailsStartupRegistration()
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["Integrios:DeliveryApi:MaxAttemptDuration"] = "half an hour"
        });

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddDeliveryApiInfrastructureServices(configuration));

        exception.Message.ShouldContain("Integrios:DeliveryApi:MaxAttemptDuration", Case.Sensitive);
    }

    [Fact]
    public void WorkerRegistration_NeverReadsTheAttemptCeiling()
    {
        IConfiguration configuration = BuildConfiguration(new()
        {
            ["Integrios:DeliveryApi:MaxAttemptDuration"] = "not-a-timespan",
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

    private static ServiceProvider BuildDeliveryApi(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeliveryApiApplicationServices();
        services.AddDeliveryApiInfrastructureServices(BuildConfiguration(values));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=integrios;Password=integrios";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
