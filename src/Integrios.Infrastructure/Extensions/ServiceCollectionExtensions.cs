using Integrios.Domain.Abstractions.Data;
using Integrios.Domain.Abstractions.Events;
using Integrios.Domain.Abstractions.Http;
using Integrios.Domain.Abstractions.Tenants;
using Integrios.Domain.Abstractions.Worker;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Data.Events;
using Integrios.Infrastructure.Data.Tenants;
using Integrios.Infrastructure.Data.Worker;
using Integrios.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Integrios.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIntegriosInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
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
        services.AddSingleton<IEventRepository, EventRepository>();
        services.AddSingleton<IOutboxRepository, OutboxRepository>();
        services.AddSingleton<IRoutingRepository, RoutingRepository>();
        services.AddSingleton<IDeliveryAttemptRepository, DeliveryAttemptRepository>();
        services.AddHttpClient<IDeliveryClient, HttpDeliveryClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
