using System.Diagnostics.Metrics;
using Integrios.Application;
using Integrios.Application.Authoring;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Authoring.OperatorKeys;
using Integrios.Application.Authoring.Sources;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Application.Authoring.Tenants;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Delivery;
using Integrios.Application.Identity;
using Integrios.Application.Ingestion;
using Integrios.Application.Secrets;
using Integrios.Application.Transforms;
using Integrios.Infrastructure.Connectors;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Destinations;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Identity;
using Integrios.Infrastructure.OperatorKeys;
using Integrios.Infrastructure.Outbox;
using Integrios.Infrastructure.Secrets;
using Integrios.Infrastructure.Sources;
using Integrios.Infrastructure.Subscriptions;
using Integrios.Infrastructure.TenantApiKeys;
using Integrios.Infrastructure.Tenants;
using Integrios.Infrastructure.Topics;
using Integrios.Infrastructure.Transforms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Integrios.Infrastructure;

public static class DependencyInjection
{
    public static async Task MigrateDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IntegriosDbContext context = scope.ServiceProvider.GetRequiredService<IntegriosDbContext>();
        // MigrateAsync retries the migration commands themselves but reads the applied-migration
        // history outside any execution strategy, so startup still died on the first transient
        // fault. Making the whole migration the retried unit covers that read too.
        await context.Database.CreateExecutionStrategy().ExecuteAsync(
            context.Database.MigrateAsync,
            cancellationToken);
    }

    public static async Task<string> GetDatabaseMigrationInfoAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IntegriosDbContext context = scope.ServiceProvider.GetRequiredService<IntegriosDbContext>();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        string[] pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        return $"EF migrations: {applied.Length} applied, {pending.Length} pending.";
    }

    public static IServiceCollection AddAdminInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Default ring for the command-line verbs, which never issue cookies or cursors. A web host
        // must also call AddAdminDataProtection, or each process gets its own unshared ring.
        services.AddDataProtection();
        services.AddDatabaseServices(configuration);
        DatabaseProvider databaseProvider = DatabaseProviders.FromConfiguration(configuration);
        services.AddScoped<OperatorKeyRepository>();
        services.AddScoped<IOperatorKeyLookup>(provider => provider.GetRequiredService<OperatorKeyRepository>());
        services.AddScoped<IOperatorKeyLifecycle>(provider => provider.GetRequiredService<OperatorKeyRepository>());
        services.AddScoped<ITenantApiKeyRepository, TenantApiKeyRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IConnectorReader, ConnectorReader>();
        if (databaseProvider == DatabaseProvider.SqlServer)
            services.AddScoped<IConnectorManifestStore, SqlServerConnectorManifestStore>();
        else
            services.AddScoped<IConnectorManifestStore, PostgresConnectorManifestStore>();
        services.AddScoped<IDestinationRepository, DestinationRepository>();
        if (databaseProvider == DatabaseProvider.SqlServer)
            services.AddSingleton<IAuthoringLock, SqlServerAuthoringLock>();
        else
            services.AddSingleton<IAuthoringLock, PostgresAuthoringLock>();
        services.AddScoped<ITopicRepository, TopicRepository>();
        services.AddScoped<ISourceRepository, SourceRepository>();
        services.AddScoped<ISourceQueries, SourceQueries>();
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<ISubscriptionQueries, SubscriptionQueries>();
        services.AddSingleton<ITenantEventLookup, TenantEventLookup>();
        // Operator-only. Deliberately absent from AddIngestionInfrastructureServices: a destination's
        // response body is the Operator's downstream system talking, and the data plane must have no
        // way to reach it.
        services.AddSingleton<IEventDiagnosticsLookup, EventDiagnosticsLookup>();
        services.AddSingleton<ITenantEventHistory, TenantEventHistory>();
        services.AddSingleton<ITenantEventMonitoring, TenantEventMonitoring>();
        services.AddSingleton<ITenantOverview, TenantOverviewReader>();
        services.AddScoped<IOperatorIdentityStore, OperatorIdentityStore>();
        services.AddScoped<PasswordCredentialStore>();
        services.AddScoped<IPasswordCredentialLifecycle>(provider =>
            provider.GetRequiredService<PasswordCredentialStore>());
        services.AddScoped<IOperatorUserQueries>(provider =>
            provider.GetRequiredService<PasswordCredentialStore>());
        services.AddScoped<IPasswordAuthenticationStore>(provider =>
            provider.GetRequiredService<PasswordCredentialStore>());
        services.AddSingleton<IDeadLetterReplay, DeadLetterReplay>();
        services.AddDestinationAuthenticationServices(enableOAuthExecution: false);
        services.AddSourceVerificationServices();
        services.AddTransformEvaluationServices();

        return services;
    }

    public static IServiceCollection AddIngestionInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        bool enableBrokerReceiver = true)
    {
        services.AddDatabaseServices(configuration);
        DatabaseProvider databaseProvider = DatabaseProviders.FromConfiguration(configuration);
        services.AddSingleton<IActiveTenantApiKeyLookup, ActiveTenantApiKeyLookup>();
        services.AddSingleton<ITenantApiKeyUseRecorder, TenantApiKeyUseRecorder>();
        services.AddSingleton<IEventApiSourceResolver, EventApiSourceResolver>();
        services.AddSingleton<ISourceEndpointResolver, SourceEndpointResolver>();
        services.AddSourceVerificationServices();
        services.AddSingleton<IBrokerSourceReader, BrokerSourceReader>();
        services.AddSingleton(new BrokerReconcileInterval(TimeSpan.FromSeconds(
            configuration.GetValue<int?>("Integrios:BrokerSources:ReconcileSeconds") ?? 30)));
        // A read-only question about secret references must not start consuming from every broker
        // Source as a side effect of being asked.
        if (enableBrokerReceiver)
            services.AddHostedService<AzureServiceBusReceiver>();
        services.AddTransformEvaluationServices();
        if (databaseProvider == DatabaseProvider.SqlServer)
            services.AddSingleton<IEventAcceptance, SqlServerEventAcceptance>();
        else
            services.AddSingleton<IEventAcceptance, PostgresEventAcceptance>();
        services.AddSingleton<ITenantEventLookup, TenantEventLookup>();
        services.TryAddSingleton<ISourceVerificationSecretResolver, UnavailableSourceVerificationSecretResolver>();
        services.AddScoped<ISourceSecretValidationReader, SourceSecretValidationReader>();

        return services;
    }

    public static IServiceCollection AddWorkerInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDatabaseServices(configuration);
        DatabaseProvider databaseProvider = DatabaseProviders.FromConfiguration(configuration);

        DeliveryExecutionOptions deliveryOptions = ReadDeliveryOptions(configuration);
        deliveryOptions.Validate();
        services.AddSingleton(deliveryOptions);
        services.AddSingleton(new RetryPolicy(
            deliveryOptions.RetryBaseDelay,
            deliveryOptions.RetryMaxAttempts));
        services.AddSingleton<DeliveryOutcomePolicy>();

        services.AddScoped<ISecretValidationReader, SecretValidationReader>();
        if (databaseProvider == DatabaseProvider.SqlServer)
        {
            services.AddSingleton<IOutboxFanout, SqlServerOutboxFanout>();
            services.AddSingleton<ICompletedHistoryCleanup, SqlServerCompletedHistoryCleanup>();
        }
        else
        {
            services.AddSingleton<IOutboxFanout, PostgresOutboxFanout>();
            services.AddSingleton<ICompletedHistoryCleanup, PostgresCompletedHistoryCleanup>();
        }
        services.AddSingleton<IEventDeliveryQueue, EventDeliveryQueue>();
        services.AddDestinationAuthenticationServices(enableOAuthExecution: true, deliveryOptions);
        services.AddTransformEvaluationServices();
        services.TryAddSingleton<IDestinationAuthenticationSecretResolver, UnavailableDestinationAuthenticationSecretResolver>();
        services.AddHttpClient<IDeliveryClient, HttpDeliveryClient>(client =>
        {
            client.Timeout = deliveryOptions.HttpTimeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });

        return services;
    }

    private static IServiceCollection AddDatabaseServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        DatabaseProvider databaseProvider = DatabaseProviders.FromConfiguration(configuration);
        string connectionName = databaseProvider == DatabaseProvider.SqlServer ? "SqlServer" : "Postgres";
        string? connectionString = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"ConnectionStrings:{connectionName} is required.");

        services.AddDbContextFactory<IntegriosDbContext>(
            options => options.UseIntegriosProvider(databaseProvider, connectionString));

        if (databaseProvider == DatabaseProvider.SqlServer)
        {
            services.AddSingleton<IDbConnectionFactory>(provider => new SqlServerConnectionFactory(
                connectionString,
                provider.GetRequiredService<IDbContextFactory<IntegriosDbContext>>()));
            return services;
        }

        services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            return dataSourceBuilder.Build();
        });

        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();

        return services;
    }

    private static IServiceCollection AddDestinationAuthenticationServices(
        this IServiceCollection services,
        bool enableOAuthExecution,
        DeliveryExecutionOptions? deliveryOptions = null)
    {
        services.AddSingleton<IDestinationAuthenticator, ApiKeyHeaderAuthenticator>();
        services.AddSingleton<IDestinationAuthenticator, BearerTokenAuthenticator>();
        if (enableOAuthExecution)
        {
            services.AddHttpClient("oauth2-token", client => client.Timeout = deliveryOptions!.HttpTimeout)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    MeterFactory = SuppressedHttpMetricsFactory.Instance
                })
                .RemoveAllLoggers();
            services.AddSingleton<IDestinationAuthenticator>(provider => new OAuth2ClientCredentialsAuthenticator(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("oauth2-token"),
                TimeProvider.System));
        }
        else
        {
            services.AddSingleton<IDestinationAuthenticator>(
                new OAuth2ClientCredentialsAuthenticator(null, TimeProvider.System));
        }
        services.AddSingleton<IDestinationAuthenticatorRegistry, DestinationAuthenticatorRegistry>();

        return services;
    }

    private static IServiceCollection AddSourceVerificationServices(this IServiceCollection services)
    {
        services.AddSingleton<ISourceVerifier, HmacSha256SourceVerifier>();
        services.AddSingleton<ISourceVerifierRegistry, SourceVerifierRegistry>();

        return services;
    }

    private static IServiceCollection AddTransformEvaluationServices(this IServiceCollection services)
    {
        services.AddSingleton<ITransformEvaluator, JsonataTransformEvaluator>();

        return services;
    }

    private static DeliveryExecutionOptions ReadDeliveryOptions(IConfiguration configuration)
    {
        DeliveryExecutionOptions defaults = DeliveryExecutionOptions.Default;

        return new DeliveryExecutionOptions(
            ReadDuration(configuration, "Integrios:Delivery:HttpTimeout", defaults.HttpTimeout),
            ReadDuration(configuration, "Integrios:Delivery:AttemptDeadline", defaults.AttemptDeadline),
            ReadDuration(configuration, "Integrios:Delivery:LeaseDuration", defaults.LeaseDuration),
            ReadDuration(configuration, "Integrios:Delivery:ShutdownGracePeriod", defaults.ShutdownGracePeriod))
        {
            RetryBaseDelay = ReadDuration(
                configuration, "Integrios:Delivery:Retry:BaseDelay", defaults.RetryBaseDelay),
            RetryMaxAttempts = ReadInt(
                configuration, "Integrios:Delivery:Retry:MaxAttempts", defaults.RetryMaxAttempts),
        };
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        string? configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
            return fallback;

        return int.TryParse(configured, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be an integer value.");
    }

    private static TimeSpan ReadDuration(IConfiguration configuration, string key, TimeSpan fallback)
    {
        string? configured = configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
            return fallback;

        return TimeSpan.TryParse(configured, out TimeSpan parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be a TimeSpan value.");
    }

    private sealed class SuppressedHttpMetricsFactory : IMeterFactory
    {
        public static SuppressedHttpMetricsFactory Instance { get; } = new();
        private static readonly Meter Meter = new("integrios.suppressed.http");

        public Meter Create(MeterOptions options) => Meter;

        public void Dispose()
        {
        }
    }
}
