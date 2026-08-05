using System.Diagnostics;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.AcceptanceTests;

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

    public async Task<string> RunFlywayAsync(QualificationDatabase database, string command)
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

        await using IContainer flyway = builder.Build();
        await flyway.StartAsync();

        long exitCode = await flyway.GetExitCodeAsync();
        (string stdout, string stderr) = await flyway.GetLogsAsync();
        string output = stdout + stderr;

        if (exitCode != 0)
            throw new InvalidOperationException($"Flyway {command} exited with {exitCode}:{Environment.NewLine}{output}");

        return output;
    }

    public static async Task<T> ScalarAsync<T>(QualificationDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return (T)(value ?? throw new InvalidOperationException("Query returned null."));
    }

    public static Task<BootstrapProcessResult> RunProductionBootstrapAsync(
        QualificationDatabase database,
        string? secret) =>
        RunAdminProcessAsync(
            database,
            ["bootstrap"],
            "INTEGRIOS_BOOTSTRAP_ADMIN_SECRET",
            secret,
            "Production Bootstrap",
            new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            });

    public static Task<BootstrapProcessResult> RunAdminKeyRotationAsync(
        QualificationDatabase database,
        string? secret) =>
        RunAdminProcessAsync(
            database,
            ["admin-key", "rotate"],
            "INTEGRIOS_ADMIN_KEY_ROTATION_SECRET",
            secret,
            "AdminKey rotation");

    private static async Task<BootstrapProcessResult> RunAdminProcessAsync(
        QualificationDatabase database,
        IReadOnlyList<string> arguments,
        string secretVariable,
        string? secret,
        string operation,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string adminAssembly = typeof(Integrios.Admin.Bootstrap.BootstrapCli).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(adminAssembly);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["ConnectionStrings__Postgres"] = database.ConnectionString;
        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
                startInfo.Environment[key] = value;
        }
        startInfo.Environment.Remove(secretVariable);
        if (secret is not null)
            startInfo.Environment[secretVariable] = secret;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the {operation} process.");

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
            throw new TimeoutException($"{operation} did not exit within one minute.");
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
