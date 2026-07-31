using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Worker.Tests;

public sealed class WorkerLoopOptionsTests
{
    [Fact]
    public void Defaults_ReproduceCombinedWorkerCadence()
    {
        IConfiguration configuration = BuildConfiguration([]);

        FanoutLoopOptions fanout = FanoutLoopOptions.FromConfiguration(configuration);
        DeliveryLoopOptions delivery = DeliveryLoopOptions.FromConfiguration(configuration);

        Assert.Equal(10, fanout.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(2), fanout.IdlePollInterval);
        Assert.Equal(25, delivery.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(2), delivery.IdlePollInterval);
    }

    [Fact]
    public void Registration_ReadsExplicitLoopConfiguration_AndIgnoresRetiredKeys()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:IdlePollInterval"] = "00:00:00.001",
            ["Integrios:Worker:Fanout:BatchSize"] = "retired",
            ["Integrios:Worker:Fanout:IdlePollInterval"] = "retired",
            ["Integrios:Worker:Delivery:BatchSize"] = "retired",
            ["Integrios:Worker:Delivery:IdlePollInterval"] = "retired",
            ["Integrios:Worker:FanoutLoop:BatchSize"] = "4",
            ["Integrios:Worker:FanoutLoop:IdlePollInterval"] = "00:00:00.125",
            ["Integrios:Worker:DeliveryLoop:BatchSize"] = "7",
            ["Integrios:Worker:DeliveryLoop:IdlePollInterval"] = "00:00:00.250"
        });
        var services = new ServiceCollection();

        services.AddWorkerHostServices(configuration, enableBackgroundLoops: true);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(new FanoutLoopOptions(4, TimeSpan.FromMilliseconds(125)),
            provider.GetRequiredService<FanoutLoopOptions>());
        Assert.Equal(new DeliveryLoopOptions(7, TimeSpan.FromMilliseconds(250)),
            provider.GetRequiredService<DeliveryLoopOptions>());
    }

    [Theory]
    [InlineData("Integrios:Worker:FanoutLoop:BatchSize", "0")]
    [InlineData("Integrios:Worker:FanoutLoop:IdlePollInterval", "00:00:00")]
    [InlineData("Integrios:Worker:DeliveryLoop:BatchSize", "-1")]
    [InlineData("Integrios:Worker:DeliveryLoop:IdlePollInterval", "-00:00:01")]
    public void Registration_RejectsNonPositiveLoopConfiguration(string key, string value)
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [key] = value
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddWorkerHostServices(configuration, enableBackgroundLoops: true));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains("positive", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Integrios:Worker:FanoutLoop:BatchSize", "many")]
    [InlineData("Integrios:Worker:FanoutLoop:IdlePollInterval", "soon")]
    [InlineData("Integrios:Worker:DeliveryLoop:BatchSize", "many")]
    [InlineData("Integrios:Worker:DeliveryLoop:IdlePollInterval", "soon")]
    public void Registration_RejectsMalformedLoopConfiguration(string key, string value)
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [key] = value
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddWorkerHostServices(configuration, enableBackgroundLoops: true));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
