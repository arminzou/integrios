using Integrios.Application.Delivery;
using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Integrios.Worker.Tests;

public sealed class DeliveryExecutionOptionsTests
{
    [Fact]
    public void Defaults_MatchFencedLeaseTimingContract()
    {
        DeliveryExecutionOptions options = DeliveryExecutionOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(30), options.HttpTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), options.AttemptDeadline);
        Assert.Equal(TimeSpan.FromMinutes(2), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ShutdownGracePeriod);
        options.Validate();
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validate_RejectsUnsafeTimingRelationships(DeliveryExecutionOptions options, string expectedSetting)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(expectedSetting, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<DeliveryExecutionOptions, string> InvalidOptions => new()
    {
        { new(TimeSpan.Zero, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60)), "HttpTimeout" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60)), "AttemptDeadline" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60)), "LeaseDuration" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(45)), "ShutdownGracePeriod" }
    };

    [Fact]
    public void InfrastructureRegistration_AppliesConfiguredExecutionTimings()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:HttpTimeout"] = "00:00:11",
            ["Integrios:Delivery:AttemptDeadline"] = "00:00:22",
            ["Integrios:Delivery:LeaseDuration"] = "00:00:44",
            ["Integrios:Delivery:ShutdownGracePeriod"] = "00:00:33"
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegriosApplication();
        services.AddIntegriosInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        DeliveryExecutionOptions options = provider.GetRequiredService<DeliveryExecutionOptions>();
        HostOptions hostOptions = provider.GetRequiredService<IOptions<HostOptions>>().Value;
        HttpClient deliveryHttpClient = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IDeliveryClient));

        Assert.Equal(TimeSpan.FromSeconds(11), options.HttpTimeout);
        Assert.Equal(TimeSpan.FromSeconds(22), options.AttemptDeadline);
        Assert.Equal(TimeSpan.FromSeconds(44), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(33), options.ShutdownGracePeriod);
        Assert.Equal(options.HttpTimeout, deliveryHttpClient.Timeout);
        // Shared infrastructure must not couple host shutdown to delivery settings;
        // only the Worker wires ShutdownGracePeriod into HostOptions.
        Assert.Equal(new HostOptions().ShutdownTimeout, hostOptions.ShutdownTimeout);
    }

    [Fact]
    public void InfrastructureRegistration_InvalidConfiguredRelationship_FailsStartupRegistration()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:HttpTimeout"] = "00:00:30",
            ["Integrios:Delivery:AttemptDeadline"] = "00:00:20"
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddIntegriosInfrastructure(configuration));

        Assert.Contains("AttemptDeadline", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=integrios;Password=integrios";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
