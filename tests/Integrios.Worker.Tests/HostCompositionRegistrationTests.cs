using Integrios.Application;
using Integrios.Application.AdminKeys;
using Integrios.Application.ApiKeys;
using Integrios.Application.Auth;
using Integrios.Application.Connections;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Integrations;
using Integrios.Application.Outbox;
using Integrios.Application.Secrets;
using Integrios.Application.Subscriptions;
using Integrios.Application.Tenants;
using Integrios.Application.Topics;
using Integrios.Application.Transforms;
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
    public void EveryApplicationHandlerImplementation_IsRegisteredByExactlyOneHost()
    {
        using ServiceProvider admin = BuildProvider(
            services => services.AddAdminApplicationServices(),
            services => services.AddAdminInfrastructureServices(BuildConfiguration()));
        using ServiceProvider ingress = BuildProvider(
            services => services.AddIngressApplicationServices(),
            services => services.AddIngressInfrastructureServices(BuildConfiguration()));
        using ServiceProvider worker = BuildProvider(
            services => services.AddWorkerApplicationServices(),
            services => services.AddWorkerInfrastructureServices(BuildConfiguration()));

        (string Name, IServiceProvider Provider)[] hosts =
        [
            ("Admin", admin),
            ("Ingress", ingress),
            ("Worker", worker)
        ];
        HandlerRegistration[] handlerRegistrations = typeof(Integrios.Application.DependencyInjection).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(implementationType => implementationType
                .GetInterfaces()
                .Where(IsHandlerInterface)
                .Select(serviceType => new HandlerRegistration(implementationType, serviceType)))
            .ToArray();

        Assert.NotEmpty(handlerRegistrations);
        foreach (HandlerRegistration handler in handlerRegistrations)
        {
            string[] owners = hosts
                .Where(host => host.Provider
                    .GetServices(handler.ServiceType)
                    .Any(instance => instance?.GetType() == handler.ImplementationType))
                .Select(host => host.Name)
                .ToArray();

            Assert.True(
                owners.Length == 1,
                $"{handler.ImplementationType} as {handler.ServiceType} must be registered by exactly one host; found: {string.Join(", ", owners)}.");
        }
    }

    [Fact]
    public void Admin_ResolvesOnlyControlPlaneAdapters()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddAdminApplicationServices(),
            services =>
            services.AddAdminInfrastructureServices(BuildConfiguration()));

        AssertResolves<IActiveAdminKeyLookup>(provider);
        AssertResolves<IAdminKeyLifecycle>(provider);
        AssertResolves<IApiKeyRepository>(provider);
        AssertResolves<ITenantRepository>(provider);
        AssertResolves<IIntegrationCatalog>(provider);
        AssertResolves<IBuiltinIntegrationReconciler>(provider);
        AssertResolves<IConnectionRepository>(provider);
        AssertResolves<ITopicRepository>(provider);
        AssertResolves<ISubscriptionRepository>(provider);
        AssertResolves<IAuthSchemeRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);

        AssertOmits<IDurableEventAcceptance>(provider);
        AssertOmits<ITenantEventLookup>(provider);
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
            services => services.AddIngressApplicationServices(),
            services =>
            services.AddIngressInfrastructureServices(BuildConfiguration()));

        AssertResolves<IActiveApiKeyLookup>(provider);
        AssertResolves<IIntakeTopicResolver>(provider);
        AssertResolves<IDurableEventAcceptance>(provider);
        AssertResolves<ITenantEventLookup>(provider);
        AssertResolves<IDeadLetterReplay>(provider);

        AssertOmits<IActiveAdminKeyLookup>(provider);
        AssertOmits<IAdminKeyLifecycle>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IIntegrationCatalog>(provider);
        AssertOmits<IBuiltinIntegrationReconciler>(provider);
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
            services => services.AddWorkerApplicationServices(),
            services =>
            services.AddWorkerInfrastructureServices(BuildConfiguration()));

        AssertResolves<ISecretValidationCatalog>(provider);
        AssertResolves<IOutboxFanout>(provider);
        AssertResolves<ISubscriptionDeliveryQueue>(provider);
        AssertResolves<IDeliveryClient>(provider);
        AssertResolves<IAuthSchemeRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);
        AssertResolves<ISecretResolver>(provider);

        AssertOmits<IActiveAdminKeyLookup>(provider);
        AssertOmits<IAdminKeyLifecycle>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<IActiveApiKeyLookup>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<IDurableEventAcceptance>(provider);
        AssertOmits<ITenantEventLookup>(provider);
        AssertOmits<ITopicRepository>(provider);
        AssertOmits<IIntakeTopicResolver>(provider);
        AssertOmits<IDeadLetterReplay>(provider);
        AssertOmits<IIntegrationCatalog>(provider);
        AssertOmits<IBuiltinIntegrationReconciler>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
    }

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

        AssertResolves<IActiveAdminKeyLookup>(provider);
        AssertResolves<IAdminKeyLifecycle>(provider);
        AssertOmits<DeliveryExecutionOptions>(provider);
    }

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

    private static bool IsHandlerInterface(Type type)
    {
        if (!type.IsGenericType)
            return false;

        Type genericDefinition = type.GetGenericTypeDefinition();
        return genericDefinition == typeof(IRequestHandler<,>)
            || genericDefinition == typeof(IRequestHandler<>)
            || genericDefinition == typeof(INotificationHandler<>)
            || genericDefinition == typeof(IStreamRequestHandler<,>);
    }

    private static bool IsOutboxDepthMetricsRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService)
        && descriptor.ImplementationType?.Name == "OutboxDepthMetrics";

    private sealed record HandlerRegistration(Type ImplementationType, Type ServiceType);
}
