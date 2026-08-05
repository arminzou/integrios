using Integrios.Application;
using Integrios.Application.AdminKeys;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Worker.UnitTests;

public sealed class DeliveryPolicyRegistrationTests
{
    [Fact]
    public void ApplicationRegistration_DoesNotProvideDeliveryPolicies()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddApplicationServices()
            .BuildServiceProvider();

        AssertOmits<DeliveryExecutionOptions>(provider);
        AssertOmits<RetryPolicy>(provider);
        AssertOmits<DeliveryOutcomePolicy>(provider);
    }

    [Fact]
    public void Ingress_OmitsDeliveryPolicies_WhileWorkerUsesConfiguredPolicy()
    {
        IConfiguration ingressConfiguration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:Retry:BaseDelay"] = "not-a-timespan",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "not-an-integer"
        });
        using ServiceProvider ingress = BuildProvider(
            services => services.AddIngressApplicationServices(),
            services =>
            services.AddIngressInfrastructureServices(ingressConfiguration));

        AssertOmits<DeliveryExecutionOptions>(ingress);
        AssertOmits<RetryPolicy>(ingress);
        AssertOmits<DeliveryOutcomePolicy>(ingress);

        IConfiguration workerConfiguration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:Retry:BaseDelay"] = "00:00:03",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "7"
        });
        using ServiceProvider worker = BuildProvider(
            services => services.AddWorkerApplicationServices(),
            services =>
            services.AddWorkerInfrastructureServices(workerConfiguration));

        DeliveryExecutionOptions workerOptions = worker.GetRequiredService<DeliveryExecutionOptions>();
        RetryPolicy workerPolicy = worker.GetRequiredService<RetryPolicy>();
        Assert.Equal(TimeSpan.FromSeconds(3), workerOptions.RetryBaseDelay);
        Assert.Equal(7, workerOptions.RetryMaxAttempts);
        Assert.Equal(workerOptions.RetryBaseDelay, workerPolicy.BaseDelay);
        Assert.Equal(workerOptions.RetryMaxAttempts, workerPolicy.MaxAttempts);
    }

    [Fact]
    public void Admin_IgnoresMalformedWorkerDeliveryConfiguration()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:HttpTimeout"] = "not-a-timespan",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "not-an-integer"
        });

        using ServiceProvider provider = BuildProvider(
            services => services.AddAdminApplicationServices(),
            services =>
            services.AddAdminInfrastructureServices(configuration));

        AssertResolves<IAdminKeyLookup>(provider);
        AssertResolves<IAdminKeyLifecycle>(provider);
        AssertOmits<DeliveryExecutionOptions>(provider);
    }

    private static ServiceProvider BuildProvider(
        Action<IServiceCollection> addApplication,
        Action<IServiceCollection> addInfrastructure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        addApplication(services);
        addInfrastructure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        values ??= [];
        values["ConnectionStrings:Postgres"] =
            "Host=localhost;Database=integrios;Username=integrios;Password=integrios";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static void AssertResolves<T>(IServiceProvider provider) where T : notnull =>
        Assert.NotNull(provider.GetService<T>());

    private static void AssertOmits<T>(IServiceProvider provider) where T : notnull =>
        Assert.Null(provider.GetService<T>());
}
