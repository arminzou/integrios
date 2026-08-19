using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Integrios.Infrastructure.Data;

internal enum DatabaseProvider
{
    Postgres,
    SqlServer,
}

internal static class DatabaseProviders
{
    public static DatabaseProvider FromConfiguration(IConfiguration configuration) =>
        configuration["Database:Provider"]?.Trim().ToLowerInvariant() switch
        {
            null or "" or "postgres" => DatabaseProvider.Postgres,
            "sqlserver" => DatabaseProvider.SqlServer,
            string value => throw new InvalidOperationException($"Database:Provider '{value}' is not supported."),
        };

    public static DatabaseProvider FromContext(DatabaseFacade database) =>
        database.ProviderName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => DatabaseProvider.Postgres,
            "Microsoft.EntityFrameworkCore.SqlServer" => DatabaseProvider.SqlServer,
            string value => throw new InvalidOperationException($"EF Core provider '{value}' is not supported."),
            null => throw new InvalidOperationException("The EF Core database provider is not configured."),
        };
}
