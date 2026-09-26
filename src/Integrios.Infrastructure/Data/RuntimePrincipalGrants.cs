using Azure.Identity;
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
            if (!principals.Any(principal => principal.EntraObjectId is not null))
            {
                await PostgresRuntimePrincipalGrants.ApplyAsync(dataSource, null, principals, cancellationToken);
                return;
            }

            // Azure installs the pgaadauth extension only in the postgres database, so Entra
            // principals are listed and created there; roles are server-wide.
            string entraAdministration = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
            await using NpgsqlDataSource entraDataSource = DependencyInjection.BuildPostgresDataSource(
                entraAdministration,
                services.GetService<DefaultAzureCredential>());
            await PostgresRuntimePrincipalGrants.ApplyAsync(dataSource, entraDataSource, principals, cancellationToken);
        }
        else
        {
            await SqlServerRuntimePrincipalGrants.ApplyAsync(connectionString, principals, cancellationToken);
        }
    }
}
