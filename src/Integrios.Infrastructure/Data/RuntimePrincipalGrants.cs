using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Integrios.Infrastructure.Data;

internal static class RuntimePrincipalGrants
{
    public static async Task GrantAsync(
        this IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        DatabaseProvider provider = DatabaseProviders.FromConfiguration(configuration);
        IReadOnlyList<RuntimePrincipal> principals = RuntimePrincipalConfiguration.Read(configuration, provider);
        string connectionName = provider == DatabaseProvider.SqlServer ? "SqlServer" : "Postgres";
        string? connectionString = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"ConnectionStrings:{connectionName} is required.");

        if (provider == DatabaseProvider.Postgres)
        {
            NpgsqlDataSource dataSource = services.GetRequiredService<NpgsqlDataSource>();
            await PostgresRuntimePrincipalGrants.ApplyAsync(dataSource, principals, cancellationToken);
        }
        else
        {
            await SqlServerRuntimePrincipalGrants.ApplyAsync(connectionString, principals, cancellationToken);
        }
    }
}
