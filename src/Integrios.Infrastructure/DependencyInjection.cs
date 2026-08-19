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
using Integrios.Infrastructure.AdminKeys;
using Integrios.Infrastructure.ApiKeys;
using Integrios.Infrastructure.Auth;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Delivery;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Integrations;
using Integrios.Infrastructure.Outbox;
using Integrios.Infrastructure.Secrets;
using Integrios.Infrastructure.Subscriptions;
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
    public static IServiceCollection AddAdminInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDatabaseServices(configuration);
        services.AddSingleton<PostgresAdminKeyRepository>();
        services.AddSingleton<IAdminKeyLookup>(provider => provider.GetRequiredService<PostgresAdminKeyRepository>());
        services.AddSingleton<IAdminKeyLifecycle>(provider => provider.GetRequiredService<PostgresAdminKeyRepository>());
        services.AddSingleton<IApiKeyRepository, PostgresApiKeyRepository>();
        services.AddSingleton<ITenantRepository, PostgresTenantRepository>();
        services.AddSingleton<PostgresIntegrationRepository>();
        services.AddSingleton<IIntegrationCatalog>(provider => provider.GetRequiredService<PostgresIntegrationRepository>());
        services.AddSingleton<IIntegrationManifestStore>(provider => provider.GetRequiredService<PostgresIntegrationRepository>());
        services.AddSingleton<IConnectionRepository, PostgresConnectionRepository>();
        services.AddSingleton<IConnectionAuthoringLock, PostgresConnectionAuthoringLock>();
        services.AddSingleton<ITopicRepository, PostgresTopicRepository>();
        services.AddSingleton<ISubscriptionRepository, PostgresSubscriptionRepository>();
        services.AddDestinationAuthenticationServices();
        services.AddSourceAdapterServices();
        services.AddTransformEvaluationServices();

        return services;
    }

    public static IServiceCollection AddIngressInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDatabaseServices(configuration);
        services.AddSingleton<IActiveApiKeyLookup, PostgresActiveApiKeyLookup>();
        services.AddSingleton<ISourceTopicLookup, PostgresIntakeTopicResolver>();
        services.AddSingleton<ISourceEndpointResolver, PostgresSourceEndpointResolver>();
        services.AddSingleton<IEventAcceptance, PostgresEventAcceptance>();
        services.AddSingleton<ITenantEventLookup, PostgresTenantEventLookup>();
        services.AddSingleton<IDeadLetterReplay, PostgresDeadLetterReplay>();
        services.TryAddSingleton<ISourceVerificationSecretResolver, UnavailableSourceVerificationSecretResolver>();
        services.AddSourceAdapterServices();
        services.AddSingleton<IIngressSourceAdapter, VerifiedWebhookIngressAdapter>();
        services.AddSingleton<IIngressSourceAdapterRuntime, IngressSourceAdapterRuntime>();

        return services;
    }

    public static IServiceCollection AddWorkerInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDatabaseServices(configuration);

        DeliveryExecutionOptions deliveryOptions = ReadDeliveryOptions(configuration);
        deliveryOptions.Validate();
        services.AddSingleton(deliveryOptions);
        services.AddSingleton(new RetryPolicy(
            deliveryOptions.RetryBaseDelay,
            deliveryOptions.RetryMaxAttempts));
        services.AddSingleton<DeliveryOutcomePolicy>();

        services.AddSingleton<ISecretValidationCatalog, PostgresSecretValidationCatalog>();
        services.AddSingleton<IOutboxFanout, PostgresOutboxFanout>();
        services.AddSingleton<ISubscriptionDeliveryQueue, PostgresSubscriptionDeliveryQueue>();
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

        string databaseProvider = configuration["Database:Provider"]?.Trim() ?? "postgres";
        if (!databaseProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Database:Provider '{databaseProvider}' is not supported.");

        var postgresConnectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgresConnectionString))
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        services.AddDbContext<IntegriosDbContext>(options => options.UseNpgsql(postgresConnectionString));
        services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
            return dataSourceBuilder.Build();
        });

        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();

        return services;
    }

    private static IServiceCollection AddDestinationAuthenticationServices(this IServiceCollection services)
    {
        services.AddSingleton<IAuthSchemeHandler, ApiKeyHeaderAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeHandler, BearerTokenAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeRegistry, AuthSchemeRegistry>();

        return services;
    }

    private static IServiceCollection AddSourceAdapterServices(this IServiceCollection services)
    {
        services.AddSingleton<ISourceAdapterRegistry, SourceAdapterRegistry>();

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
