using Integrios.Application;
using Integrios.Application.Authoring.OperatorKeys;
using Integrios.Application.Authoring.TenantApiKeys;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Ingestion;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Secrets;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Authoring.Sources;
using Integrios.Application.Authoring.Tenants;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Transforms;
using Integrios.Infrastructure.OperatorKeys;
using Integrios.Infrastructure.TenantApiKeys;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Connectors;
using Integrios.Infrastructure.Outbox;
using Integrios.Infrastructure.Secrets;
using Integrios.Infrastructure.Subscriptions;
using Integrios.Infrastructure.Sources;
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
        await context.Database.MigrateAsync(cancellationToken);
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
        services.AddDatabaseServices(configuration);
        DatabaseProvider databaseProvider = DatabaseProviders.FromConfiguration(configuration);
        services.AddScoped<OperatorKeyRepository>();
        services.AddScoped<IOperatorKeyLookup>(provider => provider.GetRequiredService<OperatorKeyRepository>());
        services.AddScoped<IOperatorKeyLifecycle>(provider => provider.GetRequiredService<OperatorKeyRepository>());
        services.AddScoped<ITenantApiKeyRepository, TenantApiKeyRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IConnectorCatalog, ConnectorCatalog>();
        if (databaseProvider == DatabaseProvider.SqlServer)
            services.AddScoped<IConnectorManifestStore, SqlServerConnectorManifestStore>();
        else
            services.AddScoped<IConnectorManifestStore, PostgresConnectorManifestStore>();
        services.AddScoped<IConnectionRepository, ConnectionRepository>();
        if (databaseProvider == DatabaseProvider.SqlServer)
            services.AddSingleton<IConnectionAuthoringLock, SqlServerConnectionAuthoringLock>();
        else
            services.AddSingleton<IConnectionAuthoringLock, PostgresConnectionAuthoringLock>();
        services.AddScoped<ITopicRepository, TopicRepository>();
        services.AddScoped<ISourceRepository, SourceRepository>();
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddSingleton<ITenantEventLookup, TenantEventLookup>();
        services.AddSingleton<IDeadLetterReplay, DeadLetterReplay>();
        services.AddDestinationAuthenticationServices();
        services.AddTransformEvaluationServices();

        return services;
    }

    public static IServiceCollection AddIngestionInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDatabaseServices(configuration);
        DatabaseProvider databaseProvider = DatabaseProviders.FromConfiguration(configuration);
        services.AddSingleton<IActiveTenantApiKeyLookup, ActiveTenantApiKeyLookup>();
        services.AddSingleton<IEventApiSourceResolver, EventApiSourceResolver>();
        services.AddSingleton<ISourceEndpointResolver, SourceEndpointResolver>();
        services.AddTransformEvaluationServices();
        if (databaseProvider == DatabaseProvider.SqlServer)
            services.AddSingleton<IEventAcceptance, SqlServerEventAcceptance>();
        else
            services.AddSingleton<IEventAcceptance, PostgresEventAcceptance>();
        services.AddSingleton<ITenantEventLookup, TenantEventLookup>();
        services.TryAddSingleton<ISourceVerificationSecretResolver, UnavailableSourceVerificationSecretResolver>();

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

        services.AddScoped<ISecretValidationCatalog, SecretValidationCatalog>();
        if (databaseProvider == DatabaseProvider.SqlServer)
            services.AddSingleton<IOutboxFanout, SqlServerOutboxFanout>();
        else
            services.AddSingleton<IOutboxFanout, PostgresOutboxFanout>();
        services.AddSingleton<IEventDeliveryQueue, EventDeliveryQueue>();
        services.AddDestinationAuthenticationServices();
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
            options =>
            {
                if (databaseProvider == DatabaseProvider.SqlServer)
                {
                    options.UseSqlServer(
                        connectionString,
                        sql => sql.MigrationsAssembly("Integrios.Migrations.SqlServer"));
                }
                else
                {
                    options.UseNpgsql(
                        connectionString,
                        postgres => postgres.MigrationsAssembly("Integrios.Migrations.Postgres"));
                }
            });

        if (databaseProvider == DatabaseProvider.SqlServer)
        {
            services.AddSingleton<IDbConnectionFactory>(_ => new SqlServerConnectionFactory(connectionString));
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

    private static IServiceCollection AddDestinationAuthenticationServices(this IServiceCollection services)
    {
        services.AddSingleton<IDestinationAuthenticator, ApiKeyHeaderAuthenticator>();
        services.AddSingleton<IDestinationAuthenticator, BearerTokenAuthenticator>();
        services.AddSingleton<IDestinationAuthenticatorRegistry, DestinationAuthenticatorRegistry>();

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
}
