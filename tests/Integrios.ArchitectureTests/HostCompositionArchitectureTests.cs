using Integrios.Application;
using Integrios.Application.AdminKeys;
using Integrios.Application.ApiKeys;
using Integrios.Application.Auth;
using Integrios.Application.Connections;
using Integrios.Application.Delivery;
using Integrios.Application.Events;
using Integrios.Application.Connectors;
using Integrios.Application.Outbox;
using Integrios.Application.Secrets;
using Integrios.Application.Sources;
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
        [typeof(IDeadLetterReplay)] = [Host.Admin],
        [typeof(IDeliveryClient)] = [Host.Worker],
        [typeof(IEventAcceptance)] = [Host.Ingress],
        [typeof(ITenantEventLookup)] = [Host.Admin, Host.Ingress],
        [typeof(IConnectorCatalog)] = [Host.Admin],
        [typeof(IConnectorManifestStore)] = [Host.Admin],
        [typeof(ISourceEndpointResolver)] = [Host.Ingress],
        [typeof(ISourceTopicLookup)] = [Host.Ingress],
        [typeof(IOutboxFanout)] = [Host.Worker],
        [typeof(IDestinationAuthenticationSecretResolver)] = [Host.Worker],
        [typeof(ISourceVerificationSecretResolver)] = [Host.Ingress],
        [typeof(ISecretValidationCatalog)] = [Host.Worker],
        [typeof(ISourceRepository)] = [Host.Admin],
        [typeof(IEventDeliveryQueue)] = [Host.Worker],
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
        using IServiceScope adminScope = admin.CreateScope();
        using IServiceScope ingressScope = ingress.CreateScope();
        using IServiceScope workerScope = worker.CreateScope();

        (string Name, IServiceProvider Provider)[] hosts =
        [
            ("Admin", adminScope.ServiceProvider),
            ("Ingress", ingressScope.ServiceProvider),
            ("Worker", workerScope.ServiceProvider)
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
        using IServiceScope adminScope = admin.CreateScope();
        using IServiceScope ingressScope = ingress.CreateScope();
        using IServiceScope workerScope = worker.CreateScope();

        (Host Host, IServiceProvider Provider)[] providers =
        [
            (Host.Admin, adminScope.ServiceProvider),
            (Host.Ingress, ingressScope.ServiceProvider),
            (Host.Worker, workerScope.ServiceProvider)
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
        using IServiceScope scope = provider.CreateScope();

        AssertResolves<IAdminKeyLookup>(scope.ServiceProvider);
        AssertResolves<IAdminKeyLifecycle>(scope.ServiceProvider);
        AssertResolves<IApiKeyRepository>(scope.ServiceProvider);
        AssertResolves<ITenantRepository>(scope.ServiceProvider);
        AssertResolves<IConnectorCatalog>(scope.ServiceProvider);
        AssertResolves<IConnectorManifestStore>(scope.ServiceProvider);
        AssertResolves<IConnectionRepository>(scope.ServiceProvider);
        AssertResolves<IConnectionAuthoringLock>(scope.ServiceProvider);
        AssertResolves<ITopicRepository>(scope.ServiceProvider);
        AssertResolves<ISubscriptionRepository>(scope.ServiceProvider);
        AssertResolves<IAuthSchemeRegistry>(scope.ServiceProvider);
        AssertResolves<ITransformEvaluator>(scope.ServiceProvider);
        AssertResolves<ITenantEventLookup>(scope.ServiceProvider);
        AssertResolves<IDeadLetterReplay>(scope.ServiceProvider);

        AssertOmits<IEventAcceptance>(scope.ServiceProvider);
        AssertOmits<IActiveApiKeyLookup>(scope.ServiceProvider);
        AssertOmits<ISourceTopicLookup>(scope.ServiceProvider);
        AssertOmits<ISecretValidationCatalog>(scope.ServiceProvider);
        AssertOmits<IOutboxFanout>(scope.ServiceProvider);
        AssertOmits<IEventDeliveryQueue>(scope.ServiceProvider);
        AssertOmits<IDeliveryClient>(scope.ServiceProvider);
        AssertOmits<IDestinationAuthenticationSecretResolver>(scope.ServiceProvider);
        AssertOmits<ISourceVerificationSecretResolver>(scope.ServiceProvider);
        AssertOmits<DeliveryExecutionOptions>(scope.ServiceProvider);
        AssertOmits<RetryPolicy>(scope.ServiceProvider);
        AssertOmits<DeliveryOutcomePolicy>(scope.ServiceProvider);
    }

    [Fact]
    public void Ingress_ResolvesOnlyIntakePorts()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddIngressApplicationServices(),
            services => services.AddIngressInfrastructureServices(BuildConfiguration()));

        AssertResolves<IActiveApiKeyLookup>(provider);
        AssertResolves<ISourceTopicLookup>(provider);
        AssertResolves<ISourceEndpointResolver>(provider);
        AssertResolves<IEventAcceptance>(provider);
        AssertResolves<ITenantEventLookup>(provider);

        AssertOmits<IAdminKeyLookup>(provider);
        AssertOmits<IAdminKeyLifecycle>(provider);
        AssertOmits<IApiKeyRepository>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IConnectorCatalog>(provider);
        AssertOmits<IConnectorManifestStore>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<ITopicRepository>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
        AssertOmits<IOutboxFanout>(provider);
        AssertOmits<IEventDeliveryQueue>(provider);
        AssertOmits<IDeliveryClient>(provider);
        AssertOmits<IAuthSchemeRegistry>(provider);
        AssertOmits<ITransformEvaluator>(provider);
        AssertOmits<IDestinationAuthenticationSecretResolver>(provider);
        AssertOmits<IDeadLetterReplay>(provider);
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
        using IServiceScope scope = provider.CreateScope();

        AssertResolves<ISecretValidationCatalog>(scope.ServiceProvider);
        AssertResolves<IOutboxFanout>(provider);
        AssertResolves<IEventDeliveryQueue>(scope.ServiceProvider);
        AssertResolves<IDeliveryClient>(scope.ServiceProvider);
        AssertResolves<IAuthSchemeRegistry>(scope.ServiceProvider);
        AssertResolves<ITransformEvaluator>(scope.ServiceProvider);
        AssertResolves<IDestinationAuthenticationSecretResolver>(scope.ServiceProvider);
        AssertOmits<ISourceVerificationSecretResolver>(scope.ServiceProvider);
        AssertResolves<DeliveryExecutionOptions>(scope.ServiceProvider);
        AssertResolves<RetryPolicy>(scope.ServiceProvider);
        AssertResolves<DeliveryOutcomePolicy>(scope.ServiceProvider);

        AssertOmits<IAdminKeyLookup>(scope.ServiceProvider);
        AssertOmits<IAdminKeyLifecycle>(scope.ServiceProvider);
        AssertOmits<IApiKeyRepository>(scope.ServiceProvider);
        AssertOmits<IActiveApiKeyLookup>(scope.ServiceProvider);
        AssertOmits<ITenantRepository>(scope.ServiceProvider);
        AssertOmits<IConnectionRepository>(scope.ServiceProvider);
        AssertOmits<IEventAcceptance>(scope.ServiceProvider);
        AssertOmits<ITenantEventLookup>(scope.ServiceProvider);
        AssertOmits<ITopicRepository>(scope.ServiceProvider);
        AssertOmits<ISourceTopicLookup>(scope.ServiceProvider);
        AssertOmits<IDeadLetterReplay>(scope.ServiceProvider);
        AssertOmits<IConnectorCatalog>(scope.ServiceProvider);
        AssertOmits<IConnectorManifestStore>(scope.ServiceProvider);
        AssertOmits<ISubscriptionRepository>(scope.ServiceProvider);
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
