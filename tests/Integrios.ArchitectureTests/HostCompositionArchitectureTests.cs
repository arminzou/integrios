using Integrios.Application;
using Integrios.Application.Authoring.OperatorKeys;
using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Ingestion;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Secrets;
using Integrios.Application.Authoring.Sources;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Authoring.Tenants;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Transforms;
using Integrios.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.ArchitectureTests;

public sealed class HostCompositionArchitectureTests
{
    private static readonly IReadOnlyDictionary<Type, Host[]> PortOwners = new Dictionary<Type, Host[]>
    {
        [typeof(IOperatorKeyLookup)] = [Host.Admin],
        [typeof(IOperatorKeyLifecycle)] = [Host.Admin],
        [typeof(ITenantApiKeyRepository)] = [Host.Admin],
        [typeof(IActiveTenantApiKeyLookup)] = [Host.Ingestion],
        [typeof(IDestinationAuthenticator)] = [Host.Admin, Host.Worker],
        [typeof(IDestinationAuthenticatorRegistry)] = [Host.Admin, Host.Worker],
        [typeof(IConnectionRepository)] = [Host.Admin],
        [typeof(IConnectionAuthoringLock)] = [Host.Admin],
        [typeof(IDeadLetterReplay)] = [Host.Admin],
        [typeof(IDeliveryClient)] = [Host.Worker],
        [typeof(IEventAcceptance)] = [Host.Ingestion],
        [typeof(ITenantEventLookup)] = [Host.Admin, Host.Ingestion],
        [typeof(IConnectorCatalog)] = [Host.Admin],
        [typeof(IConnectorManifestStore)] = [Host.Admin],
        [typeof(ISourceEndpointResolver)] = [Host.Ingestion],
        [typeof(IEventApiSourceResolver)] = [Host.Ingestion],
        [typeof(IOutboxFanout)] = [Host.Worker],
        [typeof(IDestinationAuthenticationSecretResolver)] = [Host.Worker],
        [typeof(ISourceVerificationSecretResolver)] = [Host.Ingestion],
        [typeof(ISecretValidationCatalog)] = [Host.Worker],
        [typeof(ISourceRepository)] = [Host.Admin],
        [typeof(IEventDeliveryQueue)] = [Host.Worker],
        [typeof(ISubscriptionRepository)] = [Host.Admin],
        [typeof(ITenantRepository)] = [Host.Admin],
        [typeof(ITopicRepository)] = [Host.Admin],
        [typeof(ITransformEvaluator)] = [Host.Admin, Host.Worker, Host.Ingestion]
    };

    [Fact]
    public void EveryApplicationHandler_IsRegisteredByExactlyOneProductionHost()
    {
        using ServiceProvider admin = BuildProvider(
            services => services.AddAdminApplicationServices(),
            services => services.AddAdminInfrastructureServices(BuildConfiguration()));
        using ServiceProvider ingestion = BuildProvider(
            services => services.AddIngestionApplicationServices(),
            services => services.AddIngestionInfrastructureServices(BuildConfiguration()));
        using ServiceProvider worker = BuildProvider(
            services => services.AddWorkerApplicationServices(),
            services => services.AddWorkerInfrastructureServices(BuildConfiguration()));
        using IServiceScope adminScope = admin.CreateScope();
        using IServiceScope ingestionScope = ingestion.CreateScope();
        using IServiceScope workerScope = worker.CreateScope();

        (string Name, IServiceProvider Provider)[] hosts =
        [
            ("Admin", adminScope.ServiceProvider),
            ("Ingestion", ingestionScope.ServiceProvider),
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

    // The responsibility group a handler lives in is its production owner. This is the assertion
    // that makes the grouping load-bearing: EveryApplicationHandler_IsRegisteredByExactlyOneProductionHost
    // is satisfied by any single owner, so on its own it cannot see a handler whose group says one
    // host while a different host registers it. That gap is the silent-ownership-transfer path
    // ADR-0038 accepted before the 2026-08-24 amendment.
    private static readonly IReadOnlyDictionary<string, Host> GroupOwners = new Dictionary<string, Host>
    {
        ["Integrios.Application.Authoring"] = Host.Admin,
        ["Integrios.Application.Bootstrap"] = Host.Admin,
        ["Integrios.Application.Ingestion"] = Host.Ingestion,
        ["Integrios.Application.Delivery"] = Host.Worker
    };

    // Mirrors CrossGroupOwners in Application's DependencyInjection. Both lists exist so that a
    // cross-group ownership change has to be stated twice, deliberately, rather than drifting.
    private static readonly IReadOnlyDictionary<string, Host> CrossGroupHandlerOwners =
        new Dictionary<string, Host>
        {
            ["ReplayEventDeliveryCommandHandler"] = Host.Admin,
            ["GetEventDeliveryRecoveryQueryHandler"] = Host.Admin
        };

    [Fact]
    public void EveryHandlerGroup_MapsToTheHostThatRegistersIt()
    {
        using ServiceProvider admin = BuildProvider(
            services => services.AddAdminApplicationServices(),
            services => services.AddAdminInfrastructureServices(BuildConfiguration()));
        using ServiceProvider ingestion = BuildProvider(
            services => services.AddIngestionApplicationServices(),
            services => services.AddIngestionInfrastructureServices(BuildConfiguration()));
        using ServiceProvider worker = BuildProvider(
            services => services.AddWorkerApplicationServices(),
            services => services.AddWorkerInfrastructureServices(BuildConfiguration()));
        using IServiceScope adminScope = admin.CreateScope();
        using IServiceScope ingestionScope = ingestion.CreateScope();
        using IServiceScope workerScope = worker.CreateScope();

        (Host Host, IServiceProvider Provider)[] hosts =
        [
            (Host.Admin, adminScope.ServiceProvider),
            (Host.Ingestion, ingestionScope.ServiceProvider),
            (Host.Worker, workerScope.ServiceProvider)
        ];

        ApplicationArchitectureTests.HandlerRegistration[] handlers =
            ApplicationArchitectureTests.HandlerRegistrations().ToArray();
        Assert.NotEmpty(handlers);

        foreach (ApplicationArchitectureTests.HandlerRegistration handler in handlers)
        {
            Host[] actual = hosts
                .Where(host => host.Provider
                    .GetServices(handler.ServiceType)
                    .Any(instance => instance?.GetType() == handler.ImplementationType))
                .Select(host => host.Host)
                .ToArray();

            Host expected = ExpectedOwner(handler.ImplementationType);

            Assert.True(
                actual.Length == 1 && actual[0] == expected,
                $"{handler.ImplementationType.FullName} belongs to a group owned by {expected} but "
                + $"was registered by [{string.Join(", ", actual)}]. Group boundary is host "
                + "boundary; a handler owned by another host must be declared in CrossGroupOwners "
                + "in Application's DependencyInjection and mirrored in CrossGroupHandlerOwners here.");
        }
    }

    private static Host ExpectedOwner(Type handlerType)
    {
        if (CrossGroupHandlerOwners.TryGetValue(handlerType.Name, out Host declaredOwner))
            return declaredOwner;

        KeyValuePair<string, Host>[] matches = GroupOwners
            .Where(group => ApplicationArchitectureTests.IsInGroup(handlerType.Namespace, group.Key))
            .ToArray();

        Assert.True(
            matches.Length == 1,
            $"{handlerType.FullName} must live in exactly one owning responsibility group; "
            + $"matched {matches.Length}. Shared groups hold no handlers.");

        return matches[0].Value;
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
        using ServiceProvider ingestion = BuildProvider(
            services => services.AddIngestionApplicationServices(),
            services => services.AddIngestionInfrastructureServices(BuildConfiguration()));
        using ServiceProvider worker = BuildProvider(
            services => services.AddWorkerApplicationServices(),
            services => services.AddWorkerInfrastructureServices(BuildConfiguration()));
        using IServiceScope adminScope = admin.CreateScope();
        using IServiceScope ingestionScope = ingestion.CreateScope();
        using IServiceScope workerScope = worker.CreateScope();

        (Host Host, IServiceProvider Provider)[] providers =
        [
            (Host.Admin, adminScope.ServiceProvider),
            (Host.Ingestion, ingestionScope.ServiceProvider),
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

        AssertResolves<IOperatorKeyLookup>(scope.ServiceProvider);
        AssertResolves<IOperatorKeyLifecycle>(scope.ServiceProvider);
        AssertResolves<ITenantApiKeyRepository>(scope.ServiceProvider);
        AssertResolves<ITenantRepository>(scope.ServiceProvider);
        AssertResolves<IConnectorCatalog>(scope.ServiceProvider);
        AssertResolves<IConnectorManifestStore>(scope.ServiceProvider);
        AssertResolves<IConnectionRepository>(scope.ServiceProvider);
        AssertResolves<IConnectionAuthoringLock>(scope.ServiceProvider);
        AssertResolves<ITopicRepository>(scope.ServiceProvider);
        AssertResolves<ISubscriptionRepository>(scope.ServiceProvider);
        AssertResolves<IDestinationAuthenticatorRegistry>(scope.ServiceProvider);
        AssertResolves<ITransformEvaluator>(scope.ServiceProvider);
        AssertResolves<ITenantEventLookup>(scope.ServiceProvider);
        AssertResolves<IDeadLetterReplay>(scope.ServiceProvider);

        AssertOmits<IEventAcceptance>(scope.ServiceProvider);
        AssertOmits<IActiveTenantApiKeyLookup>(scope.ServiceProvider);
        AssertOmits<IEventApiSourceResolver>(scope.ServiceProvider);
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
    public void Ingestion_ResolvesOnlyIntakePorts()
    {
        using ServiceProvider provider = BuildProvider(
            services => services.AddIngestionApplicationServices(),
            services => services.AddIngestionInfrastructureServices(BuildConfiguration()));

        AssertResolves<IActiveTenantApiKeyLookup>(provider);
        AssertResolves<IEventApiSourceResolver>(provider);
        AssertResolves<ISourceEndpointResolver>(provider);
        AssertResolves<IEventAcceptance>(provider);
        AssertResolves<ITenantEventLookup>(provider);

        AssertOmits<IOperatorKeyLookup>(provider);
        AssertOmits<IOperatorKeyLifecycle>(provider);
        AssertOmits<ITenantApiKeyRepository>(provider);
        AssertOmits<ITenantRepository>(provider);
        AssertOmits<IConnectorCatalog>(provider);
        AssertOmits<IConnectorManifestStore>(provider);
        AssertOmits<IConnectionRepository>(provider);
        AssertOmits<ITopicRepository>(provider);
        AssertOmits<ISubscriptionRepository>(provider);
        AssertOmits<IOutboxFanout>(provider);
        AssertOmits<IEventDeliveryQueue>(provider);
        AssertOmits<IDeliveryClient>(provider);
        AssertOmits<IDestinationAuthenticatorRegistry>(provider);
        AssertResolves<ITransformEvaluator>(provider);
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
        AssertResolves<IDestinationAuthenticatorRegistry>(scope.ServiceProvider);
        AssertResolves<ITransformEvaluator>(scope.ServiceProvider);
        AssertResolves<IDestinationAuthenticationSecretResolver>(scope.ServiceProvider);
        AssertOmits<ISourceVerificationSecretResolver>(scope.ServiceProvider);
        AssertResolves<DeliveryExecutionOptions>(scope.ServiceProvider);
        AssertResolves<RetryPolicy>(scope.ServiceProvider);
        AssertResolves<DeliveryOutcomePolicy>(scope.ServiceProvider);

        AssertOmits<IOperatorKeyLookup>(scope.ServiceProvider);
        AssertOmits<IOperatorKeyLifecycle>(scope.ServiceProvider);
        AssertOmits<ITenantApiKeyRepository>(scope.ServiceProvider);
        AssertOmits<IActiveTenantApiKeyLookup>(scope.ServiceProvider);
        AssertOmits<ITenantRepository>(scope.ServiceProvider);
        AssertOmits<IConnectionRepository>(scope.ServiceProvider);
        AssertOmits<IEventAcceptance>(scope.ServiceProvider);
        AssertOmits<ITenantEventLookup>(scope.ServiceProvider);
        AssertOmits<ITopicRepository>(scope.ServiceProvider);
        AssertOmits<IEventApiSourceResolver>(scope.ServiceProvider);
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
        Ingestion,
        Worker
    }
}
