using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.AcceptanceTests;

public sealed class DatabaseLifecycleFixture : IAsyncLifetime
{
    private const string PostgresUser = "integrios";
    private const string PostgresPassword = "integrios_test";

    private readonly PostgreSqlContainer postgres;

    public DatabaseLifecycleFixture()
    {
        postgres = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
            .WithDatabase("postgres")
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .Build();
    }

    public Task InitializeAsync() => postgres.StartAsync();

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

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
            "INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET",
            secret,
            "Production Bootstrap",
            new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            });

    public static Task<BootstrapProcessResult> RunDatabaseMigrationAsync(QualificationDatabase database) =>
        RunAdminProcessAsync(
            database,
            ["database", "migrate"],
            "INTEGRIOS_UNUSED_MIGRATION_SECRET",
            null,
            "Database migration");

    public static Task<BootstrapProcessResult> RunOperatorKeyRotationAsync(
        QualificationDatabase database,
        string? secret) =>
        RunAdminProcessAsync(
            database,
            ["operator-key", "rotate"],
            "INTEGRIOS_OPERATOR_KEY_ROTATION_SECRET",
            secret,
            "OperatorKey rotation");

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

}

public sealed record QualificationDatabase(string Name, string ConnectionString);

public sealed record BootstrapProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
