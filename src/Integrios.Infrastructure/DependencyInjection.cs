using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Http.Auth;
using Integrios.Infrastructure.Http;
using Integrios.Infrastructure.Telemetry;
using Integrios.Infrastructure.Transform;
using Integrios.Infrastructure.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Integrios.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegriosInfrastructure(this IServiceCollection services, IConfiguration configuration)
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
        services.AddSingleton<IApiKeyRepository, ApiKeyRepository>();
        services.AddSingleton<IAdminKeyRepository, AdminKeyRepository>();
        services.AddSingleton<IAuthSchemeHandler, ApiKeyHeaderAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeHandler, BearerTokenAuthSchemeHandler>();
        services.AddSingleton<IAuthSchemeRegistry, AuthSchemeRegistry>();
        services.AddSingleton<ISecretResolver>(_ =>
        {
            string? provider = configuration["Integrios:Secrets:Provider"];

            return string.IsNullOrWhiteSpace(provider) || provider.Equals("env", StringComparison.OrdinalIgnoreCase)
                ? new EnvironmentSecretResolver()
                : throw new InvalidOperationException($"Unsupported secrets provider '{provider}'.");
        });
        services.AddSingleton<ITenantRepository, TenantRepository>();
        services.AddSingleton<IIntegrationRepository, IntegrationRepository>();
        services.AddSingleton<IConnectionRepository, ConnectionRepository>();
        services.AddSingleton<ITopicRepository, TopicRepository>();
        services.AddSingleton<IEventRepository, EventRepository>();
        services.AddSingleton<IOutboxFanout, PostgresOutboxFanout>();
        services.AddSingleton<ISubscriptionRepository, SubscriptionRepository>();
        services.AddSingleton<ISubscriptionDeliveryRepository, SubscriptionDeliveryRepository>();
        services.AddSingleton<ISubscriptionDeliveryQueue, PostgresSubscriptionDeliveryQueue>();
        services.AddSingleton<IDeliveryAttemptRepository, DeliveryAttemptRepository>();
        services.AddSingleton<ITransformEvaluator, JsonataTransformEvaluator>();
        services.AddHttpClient<IDeliveryClient, HttpDeliveryClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHostedService<OutboxDepthMetrics>();

        return services;
    }
}
