using System.Diagnostics;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.QualificationTests;

public sealed class DatabaseLifecycleFixture : IAsyncLifetime
{
    private const string PostgresUser = "integrios";
    private const string PostgresPassword = "integrios_test";
    private const string PostgresAlias = "postgres";

    private readonly INetwork network;
    private readonly PostgreSqlContainer postgres;
    private readonly string migrationsDirectory;

    public DatabaseLifecycleFixture()
    {
        network = new NetworkBuilder()
            .WithName($"integrios-qualification-{Guid.NewGuid():N}")
            .Build();

        postgres = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
            .WithDatabase("postgres")
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .WithNetwork(network)
            .WithNetworkAliases(PostgresAlias)
            .Build();

        migrationsDirectory = Path.Combine(ResolveRepoRoot(), "db", "migrations");
    }

    public async Task InitializeAsync()
    {
        await network.CreateAsync();
        await postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync();
        await network.DeleteAsync();
    }

    public async Task<QualificationDatabase> CreateDatabaseAsync()
    {
        string databaseName = $"qualification_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            Database = databaseName,
        }.ConnectionString;

        return new QualificationDatabase(databaseName, connectionString);
    }

    public async Task<string> RunFlywayAsync(
        QualificationDatabase database,
        string command,
        int? target = null)
    {
        var builder = new ContainerBuilder("flyway/flyway:10.22.0")
            .WithNetwork(network)
            .WithBindMount(migrationsDirectory, "/flyway/sql", AccessMode.ReadOnly)
            .WithEnvironment("FLYWAY_URL", $"jdbc:postgresql://{PostgresAlias}:5432/{database.Name}")
            .WithEnvironment("FLYWAY_USER", PostgresUser)
            .WithEnvironment("FLYWAY_PASSWORD", PostgresPassword)
            .WithEnvironment("FLYWAY_LOCATIONS", "filesystem:/flyway/sql")
            .WithCommand(command)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(
                new Regex("Successfully (applied|validated)|Schema .* is up to date", RegexOptions.IgnoreCase),
                strategy => strategy
                    .WithMode(WaitStrategyMode.OneShot)
                    .WithTimeout(TimeSpan.FromMinutes(2))));

        if (target is not null)
            builder = builder.WithEnvironment("FLYWAY_TARGET", target.Value.ToString());

        await using IContainer flyway = builder.Build();
        await flyway.StartAsync();

        long exitCode = await flyway.GetExitCodeAsync();
        (string stdout, string stderr) = await flyway.GetLogsAsync();
        string output = stdout + stderr;

        if (exitCode != 0)
            throw new InvalidOperationException($"Flyway {command} exited with {exitCode}:{Environment.NewLine}{output}");

        return output;
    }

    public static async Task ExecuteFixtureAsync(QualificationDatabase database, string fixtureName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        string sql = await File.ReadAllTextAsync(path);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ExecuteMigrationSqlAsync(QualificationDatabase database, string migrationName)
    {
        string sql = await File.ReadAllTextAsync(Path.Combine(migrationsDirectory, migrationName));

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(QualificationDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return (T)(value ?? throw new InvalidOperationException("Query returned null."));
    }

    public static async Task<BootstrapProcessResult> RunProductionBootstrapAsync(
        QualificationDatabase database,
        string? secret)
    {
        string adminAssembly = typeof(Integrios.Admin.Bootstrap.BootstrapCli).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(adminAssembly);
        startInfo.ArgumentList.Add("bootstrap");
        startInfo.Environment["ConnectionStrings__Postgres"] = database.ConnectionString;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment.Remove("INTEGRIOS_BOOTSTRAP_ADMIN_SECRET");
        if (secret is not null)
            startInfo.Environment["INTEGRIOS_BOOTSTRAP_ADMIN_SECRET"] = secret;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Bootstrap process.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Production Bootstrap did not exit within one minute.");
        }

        return new BootstrapProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Integrios.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Integrios repository root.");
    }
}

public sealed record QualificationDatabase(string Name, string ConnectionString);

public sealed record BootstrapProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
