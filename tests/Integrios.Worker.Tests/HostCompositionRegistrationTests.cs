using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Worker.Tests;

public sealed class HostCompositionRegistrationTests
{
    [Fact]
    public void Admin_ResolvesOnlyControlPlaneAdapters()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddIntegriosAdminApplication(),
            services =>
            services.AddIntegriosAdminInfrastructure(BuildConfiguration()));

        AssertResolves<IAdminKeyRepository>(provider);
        AssertResolves<IApiKeyRepository>(provider);
        AssertResolves<ITenantRepository>(provider);
        AssertResolves<IIntegrationRepository>(provider);
        AssertResolves<IConnectionRepository>(provider);
        AssertResolves<ITopicRepository>(provider);
        AssertResolves<ISubscriptionRepository>(provider);
        AssertResolves<IAuthSchemeRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);

        AssertOmits<IEventRepository>(provider);
        AssertOmits<IOutboxFanout>(provider);
        AssertOmits<ISubscriptionDeliveryQueue>(provider);
        AssertOmits<IDeliveryClient>(provider);
        AssertOmits<ISecretResolver>(provider);
        AssertOmits<DeliveryExecutionOptions>(provider);
        AssertOmits<RetryPolicy>(provider);
        AssertOmits<DeliveryOutcomePolicy>(provider);
    }

    [Fact]
    public void Ingress_ResolvesOnlyEventIntakeAndReplayAdapters()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddIntegriosIngressApplication(),
            services =>
            services.AddIntegriosIngressInfrastructure(BuildConfiguration()));

        AssertResolves<IApiKeyRepository>(provider);
        AssertResolves<ITopicRepository>(provider);
        AssertResolves<IEventRepository>(provider);
        AssertResolves<ISubscriptionDeliveryQueue>(provider);

        AssertOmits<IAdminKeyRepository>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IIntegrationRepository>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
        AssertOmits<IOutboxFanout>(provider);
        AssertOmits<IDeliveryClient>(provider);
        AssertOmits<IAuthSchemeRegistry>(provider);
        AssertOmits<ITransformEvaluator>(provider);
        AssertOmits<ISecretResolver>(provider);
    }

    [Fact]
    public void Worker_ResolvesOnlyDeliveryAndSecretValidationAdapters()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddIntegriosWorkerApplication(),
            services =>
            services.AddIntegriosWorkerInfrastructure(BuildConfiguration()));

        AssertResolves<ITenantRepository>(provider);
        AssertResolves<IConnectionRepository>(provider);
        AssertResolves<IOutboxFanout>(provider);
        AssertResolves<ISubscriptionDeliveryQueue>(provider);
        AssertResolves<IDeliveryClient>(provider);
        AssertResolves<IAuthSchemeRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);
        AssertResolves<ISecretResolver>(provider);

        AssertOmits<IAdminKeyRepository>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<IEventRepository>(provider);
        AssertOmits<ITopicRepository>(provider);
        AssertOmits<IIntegrationRepository>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
    }

    [Fact]
    public void ApplicationRegistration_DoesNotProvideDeliveryPolicies()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddIntegriosApplication()
            .BuildServiceProvider();

        AssertOmits<DeliveryExecutionOptions>(provider);
        AssertOmits<RetryPolicy>(provider);
        AssertOmits<DeliveryOutcomePolicy>(provider);
    }

    [Fact]
    public void Ingress_UsesStandaloneDeliveryDefaults_WhileWorkerReplacesThemFromConfiguration()
    {
        IConfiguration ingressConfiguration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:Retry:BaseDelay"] = "not-a-timespan",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "not-an-integer"
        });
        using ServiceProvider ingress = BuildProvider(
            services => services.AddIntegriosIngressApplication(),
            services =>
            services.AddIntegriosIngressInfrastructure(ingressConfiguration));

        DeliveryExecutionOptions ingressOptions = ingress.GetRequiredService<DeliveryExecutionOptions>();
        RetryPolicy ingressPolicy = ingress.GetRequiredService<RetryPolicy>();
        Assert.Same(DeliveryExecutionOptions.Default, ingressOptions);
        Assert.Equal(RetryPolicy.DefaultBaseDelay, ingressPolicy.BaseDelay);
        Assert.Equal(RetryPolicy.DefaultMaxAttempts, ingressPolicy.MaxAttempts);

        IConfiguration workerConfiguration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Integrios:Delivery:Retry:BaseDelay"] = "00:00:03",
            ["Integrios:Delivery:Retry:MaxAttempts"] = "7"
        });
        using ServiceProvider worker = BuildProvider(
            services => services.AddIntegriosWorkerApplication(),
            services =>
            services.AddIntegriosWorkerInfrastructure(workerConfiguration));

        DeliveryExecutionOptions workerOptions = worker.GetRequiredService<DeliveryExecutionOptions>();
        RetryPolicy workerPolicy = worker.GetRequiredService<RetryPolicy>();
        Assert.NotSame(DeliveryExecutionOptions.Default, workerOptions);
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
            services => services.AddIntegriosAdminApplication(),
            services =>
            services.AddIntegriosAdminInfrastructure(configuration));

        AssertResolves<IAdminKeyRepository>(provider);
        AssertOmits<DeliveryExecutionOptions>(provider);
    }

    [Fact]
    public void WorkerBackgroundInstrumentation_IsOnlyRegisteredWhenExplicitlyAdded()
    {
        IConfiguration configuration = BuildConfiguration();
        var admin = new ServiceCollection();
        admin.AddIntegriosAdminApplication();
        admin.AddIntegriosAdminInfrastructure(configuration);
        admin.AddIntegriosTelemetry(configuration, "integrios-admin");

        var ingress = new ServiceCollection();
        ingress.AddIntegriosIngressApplication();
        ingress.AddIntegriosIngressInfrastructure(configuration);
        ingress.AddIntegriosTelemetry(configuration, "integrios-ingress");

        var worker = new ServiceCollection();
        worker.AddIntegriosWorkerApplication();
        worker.AddIntegriosWorkerInfrastructure(configuration);
        worker.AddIntegriosTelemetry(configuration, "integrios-worker");
        worker.AddIntegriosOutboxDepthMetrics(configuration);

        Assert.DoesNotContain(admin, IsOutboxDepthMetricsRegistration);
        Assert.DoesNotContain(ingress, IsOutboxDepthMetricsRegistration);
        Assert.Single(worker, IsOutboxDepthMetricsRegistration);
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

    private static bool IsOutboxDepthMetricsRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService)
        && descriptor.ImplementationType?.Name == "OutboxDepthMetrics";
}
