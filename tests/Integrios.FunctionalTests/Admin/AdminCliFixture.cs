using System.Diagnostics;
using Integrios.Admin;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Integrios.FunctionalTests.Admin;

public sealed class AdminCliFixture : IAsyncLifetime
{
    private const string PostgresUser = "integrios";
    private const string PostgresPassword = "integrios_test";

    private readonly PostgreSqlContainer postgres;

    public AdminCliFixture()
    {
        postgres = new PostgreSqlBuilder("postgres:16.14-alpine3.24")
            .WithDatabase("postgres")
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .Build();
    }

    public Task InitializeAsync() => postgres.StartAsync();

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    public async Task<CliDatabase> CreateDatabaseAsync()
    {
        string databaseName = $"cli_{Guid.NewGuid():N}";

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

        return new CliDatabase(databaseName, connectionString);
    }

    public static async Task<T> ScalarAsync<T>(CliDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return (T)(value ?? throw new InvalidOperationException("Query returned null."));
    }

    public static Task<AdminProcessResult> RunProductionSetupAsync(
        CliDatabase database,
        string? secret) =>
        RunAdminProcessAsync(
            database,
            ["setup"],
            "INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET",
            secret,
            "Production setup",
            new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["Database__RuntimePrincipals__0__Name"] = "admin_runtime",
                ["Database__RuntimePrincipals__0__Scope"] = "control-plane",
                ["Database__RuntimePrincipals__0__Password"] = "admin_runtime_test",
                ["Database__RuntimePrincipals__1__Name"] = "data_runtime",
                ["Database__RuntimePrincipals__1__Scope"] = "data-plane",
                ["Database__RuntimePrincipals__1__Password"] = "data_runtime_test",
            });

    public static Task<AdminProcessResult> RunDatabaseMigrationAsync(CliDatabase database) =>
        RunAdminProcessAsync(
            database,
            ["database", "migrate"],
            "INTEGRIOS_UNUSED_MIGRATION_SECRET",
            null,
            "Database migration");

    public static Task<AdminProcessResult> RunOperatorKeyRotationAsync(
        CliDatabase database,
        string? secret) =>
        RunAdminProcessAsync(
            database,
            ["operator-key", "rotate"],
            "INTEGRIOS_OPERATOR_KEY_ROTATION_SECRET",
            secret,
            "OperatorKey rotation");

    private static async Task<AdminProcessResult> RunAdminProcessAsync(
        CliDatabase database,
        IReadOnlyList<string> arguments,
        string secretVariable,
        string? secret,
        string operation,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(typeof(HostSelector).Assembly.Location);
        startInfo.ArgumentList.Add(AdminHost.Name);
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

        return new AdminProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

}

public sealed record CliDatabase(string Name, string ConnectionString);

public sealed record AdminProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
