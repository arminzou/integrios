using System.Data.Common;
using Dapper;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Integrios.FunctionalTests.Infrastructure;

public sealed class RuntimePrincipalGrantTests : IAsyncLifetime
{
    private const string DatabaseName = "integrios_runtime_grants";
    private const string AdminName = "runtime_admin_test";
    private const string WorkerName = "runtime_worker_test";
    private const string OldPassword = "RuntimeOld2026!";
    private const string NewPassword = "RuntimeNew2026!";

    private readonly FunctionalDatabase database = new(migrateOnStart: false);
    private string connectionString = null!;
    private IConfiguration ownerConfiguration = null!;

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        connectionString = database.ConnectionString;
        if (database.Provider == "sqlserver")
        {
            await using var master = new SqlConnection(connectionString);
            await master.OpenAsync();
            await master.ExecuteAsync("EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;");
            await master.ExecuteAsync($"CREATE DATABASE [{DatabaseName}] CONTAINMENT = PARTIAL;");
            connectionString = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = DatabaseName,
            }.ConnectionString;
        }

        ownerConfiguration = BuildConfiguration(
            ("Database:Provider", database.Provider),
            ($"ConnectionStrings:{(database.Provider == "sqlserver" ? "SqlServer" : "Postgres")}", connectionString));
        using ServiceProvider owner = new ServiceCollection()
            .AddAdminInfrastructureServices(ownerConfiguration)
            .BuildServiceProvider();
        await owner.MigrateDatabaseAsync();
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task RuntimePrincipals_ConvergeScopesAndPasswordsWithoutGrantingDdl()
    {
        await Apply(
            (AdminName, "control-plane", OldPassword),
            (WorkerName, "data-plane", OldPassword));

        await AssertCanUpdateAsync(WorkerName, OldPassword);
        await AssertCanReadOperatorTablesAsync(AdminName, OldPassword);
        await AssertDeniedAsync(WorkerName, OldPassword,
            "SELECT * FROM operator_keys",
            "DELETE FROM operator_keys WHERE 1=0",
            "SELECT * FROM operator_identities",
            "DELETE FROM operator_identities WHERE 1=0",
            "SELECT * FROM password_credentials",
            "DELETE FROM password_credentials WHERE 1=0",
            "SELECT * FROM users",
            "DELETE FROM users WHERE 1=0",
            database.Provider == "postgres"
                ? "CREATE TABLE public.runtime_ddl_probe (id integer)"
                : "CREATE TABLE dbo.runtime_ddl_probe (id int)");

        await CreateFutureTableAndReadAsync(WorkerName, OldPassword);

        // A supplied password replaces the principal password on every run.
        await Apply((WorkerName, "data-plane", NewPassword));
        await AssertConnectionRejectedAsync(WorkerName, OldPassword);
        await AssertCanUpdateAsync(WorkerName, NewPassword);

        // Omitting credential keys is grant-only mode; it preserves the existing principal.
        await Apply((WorkerName, "data-plane", null));
        await AssertCanUpdateAsync(WorkerName, NewPassword);

        // Moving control-plane to data-plane removes direct Operator-authentication access.
        await Apply((AdminName, "data-plane", OldPassword));
        await AssertDeniedAsync(AdminName, OldPassword, "SELECT * FROM operator_keys");
        await AssertCanUpdateAsync(AdminName, OldPassword);
    }

    [Fact]
    public async Task RuntimePrincipalGrant_RejectsSelfAndPrivilegedTargets()
    {
        await using DbConnection owner = database.CreateConnection();
        await owner.OpenAsync();
        string currentPrincipal = database.Provider == "postgres"
            ? await owner.ExecuteScalarAsync<string>("SELECT current_user") ?? throw new InvalidOperationException("PostgreSQL returned no current principal.")
            : await owner.ExecuteScalarAsync<string>("SELECT USER_NAME()") ?? throw new InvalidOperationException("SQL Server returned no current principal.");
        await Should.ThrowAsync<InvalidOperationException>(
            () => Apply((currentPrincipal, "control-plane", OldPassword)));

        string privilegedPrincipal = database.Provider == "postgres" ? "postgres" : "db_owner";
        await Should.ThrowAsync<InvalidOperationException>(
            () => Apply((privilegedPrincipal, "control-plane", OldPassword)));

        if (database.Provider == "sqlserver")
        {
            await Apply((WorkerName, "control-plane", OldPassword));
            await Should.ThrowAsync<InvalidOperationException>(
                () => ApplyEntraClientId(WorkerName, Guid.NewGuid()));
        }
    }

    [Fact]
    public async Task RuntimePrincipalGrant_RevokesGrantablePermissions()
    {
        if (database.Provider != "sqlserver")
            return;

        await Apply((WorkerName, "control-plane", OldPassword));
        await ExecuteAsOwnerAsync($"GRANT SELECT ON OBJECT::dbo.tenants TO [{WorkerName}] WITH GRANT OPTION;");

        await Apply((WorkerName, "control-plane", null));
        await AssertCanUpdateAsync(WorkerName, OldPassword);
        await using var owner = new SqlConnection(connectionString);
        (await owner.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = USER_ID(@Name) AND state = 'W'",
            new { Name = WorkerName })).ShouldBe(0);
    }

    [Fact]
    public async Task RuntimePrincipalGrant_BlocksImpersonationEscalation()
    {
        if (database.Provider != "sqlserver")
            return;

        await Apply((WorkerName, "data-plane", OldPassword));

        // A direct grant on a user securable is revoked, so the runtime cannot become dbo.
        await ExecuteAsOwnerAsync($"GRANT IMPERSONATE ON USER::dbo TO [{WorkerName}];");
        await Apply((WorkerName, "data-plane", null));
        await AssertDeniedAsync(WorkerName, OldPassword,
            "EXECUTE AS USER = 'dbo'; CREATE TABLE dbo.runtime_impersonation_probe (id int);");

        // The same authority inherited through a custom role is refused outright.
        await ExecuteAsOwnerAsync(
            "CREATE ROLE runtime_escalation;"
            + " GRANT IMPERSONATE ON USER::dbo TO runtime_escalation;"
            + " GRANT ALTER ANY USER TO runtime_escalation;"
            + $" ALTER ROLE runtime_escalation ADD MEMBER [{WorkerName}];");
        await Should.ThrowAsync<InvalidOperationException>(
            () => Apply((WorkerName, "data-plane", null)));
    }

    private async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var owner = new SqlConnection(connectionString);
        await owner.OpenAsync();
        await owner.ExecuteAsync(sql);
    }

    private async Task ApplyEntraClientId(string name, Guid clientId)
    {
        IConfiguration configuration = BuildConfiguration(
            ("Database:Provider", database.Provider),
            ("ConnectionStrings:SqlServer", connectionString),
            ("Database:RuntimePrincipals:0:Name", name),
            ("Database:RuntimePrincipals:0:Scope", "control-plane"),
            ("Database:RuntimePrincipals:0:EntraClientId", clientId.ToString("D")));
        using ServiceProvider provider = new ServiceCollection()
            .AddAdminInfrastructureServices(configuration)
            .BuildServiceProvider();
        await provider.GrantRuntimePrincipalsAsync(configuration);
    }

    private async Task Apply(params (string Name, string Scope, string? Password)[] principals)
    {
        var values = new List<(string Key, string? Value)>
        {
            ("Database:Provider", database.Provider),
            ($"ConnectionStrings:{(database.Provider == "sqlserver" ? "SqlServer" : "Postgres")}", connectionString),
        };
        for (int i = 0; i < principals.Length; i++)
        {
            values.Add(($"Database:RuntimePrincipals:{i}:Name", principals[i].Name));
            values.Add(($"Database:RuntimePrincipals:{i}:Scope", principals[i].Scope));
            if (principals[i].Password is not null)
                values.Add(($"Database:RuntimePrincipals:{i}:Password", principals[i].Password));
        }

        IConfiguration configuration = BuildConfiguration([.. values]);
        using ServiceProvider provider = new ServiceCollection()
            .AddAdminInfrastructureServices(configuration)
            .BuildServiceProvider();
        await provider.GrantRuntimePrincipalsAsync(configuration);
    }

    private async Task AssertCanUpdateAsync(string name, string password)
    {
        await using DbConnection connection = CreateRuntimeConnection(name, password);
        await connection.OpenAsync();
        string schema = database.Provider == "postgres" ? string.Empty : "dbo.";
        await connection.ExecuteAsync($"UPDATE {schema}tenants SET status = status WHERE 1=0");
    }

    private async Task AssertCanReadOperatorTablesAsync(string name, string password)
    {
        await using DbConnection connection = CreateRuntimeConnection(name, password);
        await connection.OpenAsync();
        foreach (string table in OperatorTables())
            await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {TableName(table)}");
    }

    private async Task AssertDeniedAsync(string name, string password, params string[] statements)
    {
        foreach (string statement in statements)
        {
            await using DbConnection connection = CreateRuntimeConnection(name, password);
            await connection.OpenAsync();
            await Should.ThrowAsync<DbException>(() => connection.ExecuteAsync(statement));
        }
    }

    private async Task AssertConnectionRejectedAsync(string name, string password)
    {
        await using DbConnection connection = CreateRuntimeConnection(name, password);
        await Should.ThrowAsync<DbException>(() => connection.OpenAsync());
    }

    private async Task CreateFutureTableAndReadAsync(string name, string password)
    {
        await using DbConnection owner = database.Provider == "postgres"
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);
        await owner.OpenAsync();
        string create = database.Provider == "postgres"
            ? "CREATE TABLE public.runtime_future_probe (id integer)"
            : "CREATE TABLE dbo.runtime_future_probe (id int)";
        await owner.ExecuteAsync(create);

        await using DbConnection runtime = CreateRuntimeConnection(name, password);
        await runtime.OpenAsync();
        await runtime.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {TableName("runtime_future_probe")}");
    }

    private DbConnection CreateRuntimeConnection(string name, string password)
    {
        if (database.Provider == "postgres")
            return new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            {
                Username = name,
                Password = password,
                Pooling = false,
            }.ConnectionString);

        return new SqlConnection(new SqlConnectionStringBuilder(connectionString)
        {
            UserID = name,
            Password = password,
            Pooling = false,
        }.ConnectionString);
    }

    private IEnumerable<string> OperatorTables() =>
    [
        "operator_keys",
        "operator_identities",
        "password_credentials",
        "users",
    ];

    private string TableName(string table) => database.Provider == "postgres" ? $"public.{table}" : $"dbo.{table}";

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();
}
