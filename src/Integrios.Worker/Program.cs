using Integrios.Worker;
using Integrios.Worker.Infrastructure.Data;
using Integrios.Worker.Infrastructure.Http;
using Npgsql;

namespace Integrios.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgresConnectionString))
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        builder.Services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
            return dataSourceBuilder.Build();
        });

        builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
        builder.Services.AddSingleton<IOutboxRepository, OutboxRepository>();
        builder.Services.AddSingleton<IRoutingRepository, RoutingRepository>();

        builder.Services.AddHttpClient<IDeliveryClient, HttpDeliveryClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        builder.Services.AddHostedService<OutboxWorker>();

        var host = builder.Build();
        host.Run();
    }
}
