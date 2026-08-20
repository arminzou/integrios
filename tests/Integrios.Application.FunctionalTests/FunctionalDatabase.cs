using System.Data.Common;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Integrios.Application.FunctionalTests;

internal sealed class FunctionalDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer? postgres;
    private readonly MsSqlContainer? sqlServer;
    public FunctionalDatabase()
    {
        Provider = (Environment.GetEnvironmentVariable("INTEGRIOS_TEST_DATABASE_PROVIDER") ?? "postgres")
            .Trim().ToLowerInvariant();
        switch (Provider)
        {
            case "postgres":
                postgres = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
                    .WithDatabase("integrios").WithUsername("postgres").WithPassword("postgres").Build();
                break;
            case "sqlserver":
                sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04")
                    .WithPassword("Integrios_Test_2026!").Build();
                break;
            default:
                throw new InvalidOperationException(
                    "INTEGRIOS_TEST_DATABASE_PROVIDER must be postgres or sqlserver.");
        }
    }

    public string Provider { get; }
    public string ConnectionName => Provider == "sqlserver" ? "SqlServer" : "Postgres";
    public string ConnectionString => postgres?.GetConnectionString() ?? sqlServer!.GetConnectionString();
    public string Now => Provider == "postgres" ? "now()" : "SYSUTCDATETIME()";
    public string OneSecondAgo => Provider == "postgres"
        ? "now() - interval '1 second'"
        : "DATEADD(second, -1, SYSUTCDATETIME())";
    public string KeyColumn => Provider == "sqlserver" ? "[key]" : "key";
    public string Json(string parameter) => Provider == "postgres" ? $"{parameter}::jsonb" : parameter;
    public string JsonText(string column) => Provider == "postgres" ? $"{column}::text" : column;

    public IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Database:Provider"] = Provider,
            [$"ConnectionStrings:{ConnectionName}"] = ConnectionString
        }).Build();

    public async Task StartAsync()
    {
        if (postgres is not null) await postgres.StartAsync();
        else await sqlServer!.StartAsync();

        using ServiceProvider provider = new ServiceCollection()
            .AddAdminInfrastructureServices(Configuration)
            .BuildServiceProvider();
        await provider.MigrateDatabaseAsync();
    }

    public DbConnection CreateConnection() => postgres is not null
        ? new NpgsqlConnection(ConnectionString)
        : new SqlConnection(ConnectionString);

    public DbContextOptions<IntegriosDbContext> CreateOptions()
    {
        var builder = new DbContextOptionsBuilder<IntegriosDbContext>();
        if (postgres is not null) builder.UseNpgsql(ConnectionString);
        else builder.UseSqlServer(
            ConnectionString, options => options.MigrationsAssembly("Integrios.Migrations.SqlServer"));
        return builder.Options;
    }

    public async Task<Respawner> CreateRespawnerAsync()
    {
        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();
        IDbAdapter adapter = postgres is not null ? DbAdapter.Postgres : DbAdapter.SqlServer;
        RespawnerOptions options = new()
        {
            DbAdapter = adapter,
            SchemasToInclude = [Provider == "postgres" ? "public" : "dbo"],
            TablesToIgnore = [new Respawn.Graph.Table(
                Provider == "postgres" ? "public" : "dbo", "__EFMigrationsHistory")]
        };
        return await Respawner.CreateAsync(connection, options);
    }

    public async ValueTask DisposeAsync()
    {
        if (postgres is not null) await postgres.DisposeAsync();
        else await sqlServer!.DisposeAsync();
    }
}
