using Integrios.Application;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Application.Delivery;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Http.Auth;
using Integrios.Infrastructure.Http;
using Integrios.Infrastructure.Transform;
using Integrios.Infrastructure.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Integrios.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegriosAdminInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIntegriosPostgres(configuration);
        services.AddSingleton<IAdminKeyRepository, AdminKeyRepository>();
        services.AddSingleton<IApiKeyRepository, ApiKeyRepository>();
        services.AddSingleton<ITenantRepository, TenantRepository>();
        services.AddSingleton<IIntegrationRepository, IntegrationRepository>();
        services.AddSingleton<IConnectionRepository, ConnectionRepository>();
        services.AddSingleton<ITopicRepository, TopicRepository>();
        services.AddSingleton<ISubscriptionRepository, SubscriptionRepository>();
        services.AddIntegriosConnectionAuthoring();

        return services;
    }

    public static IServiceCollection AddIntegriosIngressInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIntegriosPostgres(configuration);
        services.AddIntegriosDeliveryPolicies();
        services.AddSingleton<IApiKeyRepository, ApiKeyRepository>();
        services.AddSingleton<ITopicRepository, TopicRepository>();
        services.AddSingleton<IEventRepository, EventRepository>();
        services.AddSingleton<ISubscriptionDeliveryQueue, PostgresSubscriptionDeliveryQueue>();

        return services;
    }

    public static IServiceCollection AddIntegriosWorkerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddIntegriosPostgres(configuration);
        services.AddIntegriosDeliveryPolicies();

        DeliveryExecutionOptions deliveryOptions = ReadDeliveryOptions(configuration);
        deliveryOptions.Validate();
        services.Replace(ServiceDescriptor.Singleton(deliveryOptions));

        // The delivery capability registers a stand-alone default for Ingress replay. Worker owns
        // delivery configuration, so its configured policy deliberately replaces that default.
        services.Replace(ServiceDescriptor.Singleton(
            new RetryPolicy(deliveryOptions.RetryBaseDelay, deliveryOptions.RetryMaxAttempts)));

        services.AddSingleton<ITenantRepository, TenantRepository>();
        services.AddSingleton<IConnectionRepository, ConnectionRepository>();
        services.AddSingleton<IOutboxFanout, PostgresOutboxFanout>();
        services.AddSingleton<ISubscriptionDeliveryQueue, PostgresSubscriptionDeliveryQueue>();
        services.AddIntegriosConnectionAuthoring();
        services.TryAddSingleton<ISecretResolver, UnavailableSecretResolver>();
        services.AddHttpClient<IDeliveryClient, HttpDeliveryClient>(client =>
        {
            client.Timeout = deliveryOptions.HttpTimeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });

        return services;
    }

    private static IServiceCollection AddIntegriosPostgres(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        var postgresConnectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgresConnectionString))
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
            return dataSourceBuilder.Build();
        });

        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();

        return services;
    }

    private static IServiceCollection AddIntegriosConnectionAuthoring(this IServiceCollection services)
    {
        services.AddSingleton<IAuthSchemeHandler, ApiKeyHeaderAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeHandler, BearerTokenAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeRegistry, AuthSchemeRegistry>();
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
            IdlePollInterval = ReadDuration(
                configuration, "Integrios:Delivery:IdlePollInterval", defaults.IdlePollInterval),
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
