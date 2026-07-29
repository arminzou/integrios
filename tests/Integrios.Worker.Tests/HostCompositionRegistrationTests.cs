using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.ApiKeys;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Secrets;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Worker.Tests;

public sealed class HostCompositionRegistrationTests
{
    [Fact]
    public void EveryApplicationHandler_IsRegisteredByExactlyOneHost()
    {
        using ServiceProvider admin = BuildProvider(
            services => services.AddIntegriosAdminApplication(),
            services => services.AddIntegriosAdminInfrastructure(BuildConfiguration()));
        using ServiceProvider ingress = BuildProvider(
            services => services.AddIntegriosIngressApplication(),
            services => services.AddIntegriosIngressInfrastructure(BuildConfiguration()));
        using ServiceProvider worker = BuildProvider(
            services => services.AddIntegriosWorkerApplication(),
            services => services.AddIntegriosWorkerInfrastructure(BuildConfiguration()));

        (string Name, IServiceProvider Provider)[] hosts =
        [
            ("Admin", admin),
            ("Ingress", ingress),
            ("Worker", worker)
        ];
        Type[] handlerInterfaces = typeof(Integrios.Application.DependencyInjection).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(type => type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(handlerInterfaces);
        foreach (Type handlerInterface in handlerInterfaces)
        {
            string[] owners = hosts
                .Where(host => host.Provider.GetService(handlerInterface) is not null)
                .Select(host => host.Name)
                .ToArray();

            Assert.True(
                owners.Length == 1,
                $"{handlerInterface} must be registered by exactly one host; found: {string.Join(", ", owners)}.");
        }
    }

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
        AssertOmits<IActiveApiKeyLookup>(provider);
        AssertOmits<IIntakeTopicResolver>(provider);
        AssertOmits<IDeadLetterReplay>(provider);
        AssertOmits<ISecretValidationCatalog>(provider);
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

        AssertResolves<IActiveApiKeyLookup>(provider);
        AssertResolves<IIntakeTopicResolver>(provider);
        AssertResolves<IEventRepository>(provider);
        AssertResolves<IDeadLetterReplay>(provider);

        AssertOmits<IAdminKeyRepository>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IIntegrationRepository>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
        AssertOmits<IOutboxFanout>(provider);
        AssertOmits<ISubscriptionDeliveryQueue>(provider);
        AssertOmits<IDeliveryClient>(provider);
        AssertOmits<IAuthSchemeRegistry>(provider);
        AssertOmits<ITransformEvaluator>(provider);
        AssertOmits<ISecretResolver>(provider);
        AssertOmits<DeliveryExecutionOptions>(provider);
        AssertOmits<RetryPolicy>(provider);
        AssertOmits<DeliveryOutcomePolicy>(provider);
    }

    [Fact]
    public void Worker_ResolvesOnlyDeliveryAndSecretValidationAdapters()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddIntegriosWorkerApplication(),
            services =>
            services.AddIntegriosWorkerInfrastructure(BuildConfiguration()));

        AssertResolves<ISecretValidationCatalog>(provider);
        AssertResolves<IOutboxFanout>(provider);
        AssertResolves<ISubscriptionDeliveryQueue>(provider);
        AssertResolves<IDeliveryClient>(provider);
        AssertResolves<IAuthSchemeRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);
        AssertResolves<ISecretResolver>(provider);

        AssertOmits<IAdminKeyRepository>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<IActiveApiKeyLookup>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<IEventRepository>(provider);
        AssertOmits<ITopicRepository>(provider);
        AssertOmits<IIntakeTopicResolver>(provider);
        AssertOmits<IDeadLetterReplay>(provider);
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
    public void Ingress_OmitsDeliveryPolicies_WhileWorkerUsesConfiguredPolicy()
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

        AssertOmits<DeliveryExecutionOptions>(ingress);
        AssertOmits<RetryPolicy>(ingress);
        AssertOmits<DeliveryOutcomePolicy>(ingress);

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
