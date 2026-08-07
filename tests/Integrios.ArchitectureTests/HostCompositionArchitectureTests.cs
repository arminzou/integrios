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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.ArchitectureTests;

public sealed class HostCompositionArchitectureTests
{
    private static readonly IReadOnlyDictionary<Type, Host[]> PortOwners = new Dictionary<Type, Host[]>
    {
        [typeof(IAdminKeyLookup)] = [Host.Admin],
        [typeof(IAdminKeyLifecycle)] = [Host.Admin],
        [typeof(IApiKeyRepository)] = [Host.Admin],
        [typeof(IActiveApiKeyLookup)] = [Host.Ingress],
        [typeof(IAuthSchemeHandler)] = [Host.Admin, Host.Worker],
        [typeof(IAuthSchemeRegistry)] = [Host.Admin, Host.Worker],
        [typeof(IConnectionRepository)] = [Host.Admin],
        [typeof(IConnectionAuthoringLock)] = [Host.Admin],
        [typeof(IDeadLetterReplay)] = [Host.Ingress],
        [typeof(IDeliveryClient)] = [Host.Worker],
        [typeof(IEventAcceptance)] = [Host.Ingress],
        [typeof(ITenantEventLookup)] = [Host.Ingress],
        [typeof(IIntegrationCatalog)] = [Host.Admin],
        [typeof(IIntegrationManifestStore)] = [Host.Admin],
        [typeof(ISourceAdapterRegistry)] = [Host.Admin, Host.Ingress],
        [typeof(IIngressSourceAdapter)] = [Host.Ingress],
        [typeof(IIngressSourceAdapterRuntime)] = [Host.Ingress],
        [typeof(ISourceEndpointResolver)] = [Host.Ingress],
        [typeof(ISourceTopicLookup)] = [Host.Ingress],
        [typeof(IOutboxFanout)] = [Host.Worker],
        [typeof(IDestinationAuthenticationSecretResolver)] = [Host.Worker],
        [typeof(ISourceVerificationSecretResolver)] = [Host.Ingress],
        [typeof(ISecretValidationCatalog)] = [Host.Worker],
        [typeof(ISubscriptionDeliveryQueue)] = [Host.Worker],
        [typeof(ISubscriptionRepository)] = [Host.Admin],
        [typeof(ITenantRepository)] = [Host.Admin],
        [typeof(ITopicRepository)] = [Host.Admin],
        [typeof(ITransformEvaluator)] = [Host.Admin, Host.Worker]
    };

    [Fact]
    public void EveryApplicationHandler_IsRegisteredByExactlyOneProductionHost()
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

        ApplicationArchitectureTests.HandlerRegistration[] handlers =
            ApplicationArchitectureTests.HandlerRegistrations().ToArray();
        Assert.NotEmpty(handlers);

        foreach (ApplicationArchitectureTests.HandlerRegistration handler in handlers)
        {
            string[] owners = hosts
                .Where(host => host.Provider
                    .GetServices(handler.ServiceType)
                    .Any(instance => instance?.GetType() == handler.ImplementationType))
                .Select(host => host.Name)
                .ToArray();

            Assert.True(
                owners.Length == 1,
                $"{handler.ImplementationType.FullName} as {handler.ServiceType} must be registered by exactly one host; found: {string.Join(", ", owners)}.");
        }
    }

    [Fact]
    public void EveryPublicApplicationPort_HasExplicitAndExactHostOwnership()
    {
        Type[] publicPorts = ApplicationArchitectureTests.ApplicationAssembly.GetExportedTypes()
            .Where(type => type.IsInterface)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Type[] classifiedPorts = PortOwners.Keys
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        string[] unclassified = publicPorts.Except(classifiedPorts).Select(type => type.FullName!).ToArray();
        string[] retired = classifiedPorts.Except(publicPorts).Select(type => type.FullName!).ToArray();

        Assert.True(
            unclassified.Length == 0 && retired.Length == 0,
            "Every public Application port must appear in PortOwners with its exact host set, so a "
            + $"new port cannot silently escape ownership review. {ProjectArchitectureTests.DescribeSetDiff(unclassified, retired)}");

        using ServiceProvider admin = BuildProvider(
            services => services.AddAdminApplicationServices(),
            services => services.AddAdminInfrastructureServices(BuildConfiguration()));
        using ServiceProvider ingress = BuildProvider(
            services => services.AddIngressApplicationServices(),
            services => services.AddIngressInfrastructureServices(BuildConfiguration()));
        using ServiceProvider worker = BuildProvider(
            services => services.AddWorkerApplicationServices(),
            services => services.AddWorkerInfrastructureServices(BuildConfiguration()));

        (Host Host, IServiceProvider Provider)[] providers =
        [
            (Host.Admin, admin),
            (Host.Ingress, ingress),
            (Host.Worker, worker)
        ];

        foreach ((Type port, Host[] expectedOwners) in PortOwners)
        {
            foreach ((Host host, IServiceProvider provider) in providers)
            {
                bool resolves = provider.GetServices(port).Any();
                Assert.True(
                    resolves == expectedOwners.Contains(host),
                    $"{port.FullName} ownership for {host} was expected={expectedOwners.Contains(host)} but resolved={resolves}.");
            }
        }
    }

    [Fact]
    public void Admin_ResolvesOnlyControlPlanePorts()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddAdminApplicationServices(),
            services => services.AddAdminInfrastructureServices(BuildConfiguration()));

        AssertResolves<IAdminKeyLookup>(provider);
        AssertResolves<IAdminKeyLifecycle>(provider);
        AssertResolves<IApiKeyRepository>(provider);
        AssertResolves<ITenantRepository>(provider);
        AssertResolves<IIntegrationCatalog>(provider);
        AssertResolves<IIntegrationManifestStore>(provider);
        AssertResolves<ISourceAdapterRegistry>(provider);
        AssertResolves<IConnectionRepository>(provider);
        AssertResolves<IConnectionAuthoringLock>(provider);
        AssertResolves<ITopicRepository>(provider);
        AssertResolves<ISubscriptionRepository>(provider);
        AssertResolves<IAuthSchemeRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);

        AssertOmits<IEventAcceptance>(provider);
        AssertOmits<ITenantEventLookup>(provider);
        AssertOmits<IActiveApiKeyLookup>(provider);
        AssertOmits<ISourceTopicLookup>(provider);
        AssertOmits<IDeadLetterReplay>(provider);
        AssertOmits<ISecretValidationCatalog>(provider);
        AssertOmits<IOutboxFanout>(provider);
        AssertOmits<ISubscriptionDeliveryQueue>(provider);
        AssertOmits<IDeliveryClient>(provider);
        AssertOmits<IDestinationAuthenticationSecretResolver>(provider);
        AssertOmits<ISourceVerificationSecretResolver>(provider);
        AssertOmits<DeliveryExecutionOptions>(provider);
        AssertOmits<RetryPolicy>(provider);
        AssertOmits<DeliveryOutcomePolicy>(provider);
    }

    [Fact]
    public void Ingress_ResolvesOnlyIntakeAndReplayPorts()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddIngressApplicationServices(),
            services => services.AddIngressInfrastructureServices(BuildConfiguration()));

        AssertResolves<IActiveApiKeyLookup>(provider);
        AssertResolves<ISourceTopicLookup>(provider);
        AssertResolves<ISourceEndpointResolver>(provider);
        AssertResolves<IEventAcceptance>(provider);
        AssertResolves<ITenantEventLookup>(provider);
        AssertResolves<IDeadLetterReplay>(provider);
        AssertResolves<ISourceAdapterRegistry>(provider);
        AssertResolves<IIngressSourceAdapter>(provider);
        AssertResolves<IIngressSourceAdapterRuntime>(provider);

        AssertOmits<IAdminKeyLookup>(provider);
        AssertOmits<IAdminKeyLifecycle>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IIntegrationCatalog>(provider);
        AssertOmits<IIntegrationManifestStore>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<ITopicRepository>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
        AssertOmits<IOutboxFanout>(provider);
        AssertOmits<ISubscriptionDeliveryQueue>(provider);
        AssertOmits<IDeliveryClient>(provider);
        AssertOmits<IAuthSchemeRegistry>(provider);
        AssertOmits<ITransformEvaluator>(provider);
        AssertOmits<IDestinationAuthenticationSecretResolver>(provider);
        AssertResolves<ISourceVerificationSecretResolver>(provider);
        AssertOmits<DeliveryExecutionOptions>(provider);
        AssertOmits<RetryPolicy>(provider);
        AssertOmits<DeliveryOutcomePolicy>(provider);
    }

    [Fact]
    public void Worker_ResolvesOnlyDeliveryAndSecretValidationPorts()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddWorkerApplicationServices(),
            services => services.AddWorkerInfrastructureServices(BuildConfiguration()));

        AssertResolves<ISecretValidationCatalog>(provider);
        AssertResolves<IOutboxFanout>(provider);
        AssertResolves<ISubscriptionDeliveryQueue>(provider);
        AssertResolves<IDeliveryClient>(provider);
        AssertResolves<IAuthSchemeRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);
        AssertResolves<IDestinationAuthenticationSecretResolver>(provider);
        AssertOmits<ISourceVerificationSecretResolver>(provider);
        AssertResolves<DeliveryExecutionOptions>(provider);
        AssertResolves<RetryPolicy>(provider);
        AssertResolves<DeliveryOutcomePolicy>(provider);

        AssertOmits<IAdminKeyLookup>(provider);
        AssertOmits<IAdminKeyLifecycle>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<IActiveApiKeyLookup>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<IEventAcceptance>(provider);
        AssertOmits<ITenantEventLookup>(provider);
        AssertOmits<ITopicRepository>(provider);
        AssertOmits<ISourceTopicLookup>(provider);
        AssertOmits<IDeadLetterReplay>(provider);
        AssertOmits<IIntegrationCatalog>(provider);
        AssertOmits<IIntegrationManifestStore>(provider);
        AssertOmits<ISourceAdapterRegistry>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
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

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=integrios;Username=integrios;Password=integrios"
            })
            .Build();

    private static void AssertResolves<T>(IServiceProvider provider) where T : notnull =>
        Assert.NotNull(provider.GetService<T>());

    private static void AssertOmits<T>(IServiceProvider provider) where T : notnull =>
        Assert.Null(provider.GetService<T>());

    private enum Host
    {
        Admin,
        Ingress,
        Worker
    }
}
