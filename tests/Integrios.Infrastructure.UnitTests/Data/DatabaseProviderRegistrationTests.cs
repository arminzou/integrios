using Integrios.Application.Authoring;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Ingestion;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Connectors;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Destinations;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Integrios.Infrastructure.UnitTests;

public sealed class DatabaseProviderRegistrationTests
{
    [Fact]
    public void PostgresProvider_RegistersThePostgresDbContext()
    {
        var services = new ServiceCollection();
        services.AddAdminInfrastructureServices(BuildConfiguration("postgres"));

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        IntegriosDbContext context = scope.ServiceProvider.GetRequiredService<IntegriosDbContext>();
        context.Database.ProviderName.ShouldBe("Npgsql.EntityFrameworkCore.PostgreSQL");
        ((NpgsqlConnection)context.Database.GetDbConnection()).ConnectionString.ShouldBe(
            provider.GetRequiredService<NpgsqlDataSource>().ConnectionString);
        var entityTypes = context.Model.GetEntityTypes().ToArray();
        string[] tables = entityTypes.Select(entity => entity.GetTableName()!).ToArray();
        tables.ShouldContain("tenants");
        foreach (var entity in entityTypes.Where(entity => entity.ClrType != typeof(OutboxEntry)))
            entity.ClrType.Namespace!.ShouldStartWith("Integrios.Domain.", Case.Sensitive);

        context.Model.FindEntityType(typeof(Tenant))?.GetTableName().ShouldBe("tenants");
        context.Model.FindEntityType(typeof(Event))?.GetTableName().ShouldBe("events");
        context.Model.FindEntityType(typeof(EventDelivery))?.GetTableName().ShouldBe(
            "event_deliveries");
        context.Model.FindEntityType(typeof(DeliveryAttempt))?.GetTableName().ShouldBe(
            "delivery_attempts");
        context.Model.FindEntityType(typeof(OutboxEntry))?.GetTableName().ShouldBe("outbox");

        var operatorKey = context.Model.FindEntityType(typeof(OperatorKey))!;
        operatorKey.FindProperty("TenantId").ShouldBeNull();
        var status = context.Model.FindEntityType(typeof(EventDelivery))!
            .FindProperty(nameof(EventDelivery.Status))!;
        status.GetTypeMapping().Converter!.ConvertToProvider(EventDeliveryStatus.InFlight).ShouldBe(
            "in_flight");
    }

    [Fact]
    public void PostgresAzureEntra_UsesOneDataSourceForEfAndDirectConnections()
    {
        var services = new ServiceCollection();
        services.AddAdminInfrastructureServices(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["Database:Postgres:Authentication"] = "AzureEntra",
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=integrios",
            })
            .Build());

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        var connection = (NpgsqlConnection)scope.ServiceProvider
            .GetRequiredService<IntegriosDbContext>().Database.GetDbConnection();

        connection.ConnectionString.ShouldBe(provider.GetRequiredService<NpgsqlDataSource>().ConnectionString);
        connection.ConnectionString.ShouldNotContain("Password=");
    }

    [Fact]
    public void PostgresAzureEntra_RejectsPasswordConnectionString()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminInfrastructureServices(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "postgres",
                    ["Database:Postgres:Authentication"] = "AzureEntra",
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=integrios;Password=secret",
                })
                .Build()));

        exception.Message.ShouldContain("ConnectionStrings:Postgres");
        exception.Message.ShouldNotContain("secret");
    }

    [Fact]
    public void PostgresAzureEntra_RequiresUsername()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminInfrastructureServices(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "postgres",
                    ["Database:Postgres:Authentication"] = "AzureEntra",
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios",
                })
                .Build()));

        exception.Message.ShouldContain("ConnectionStrings:Postgres:Username");
    }

    [Fact]
    public void PostgresAuthentication_RejectsUnknownModeAndSqlServerConfiguration()
    {
        InvalidOperationException modeException = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminInfrastructureServices(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "postgres",
                    ["Database:Postgres:Authentication"] = "ManagedIdentity",
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=integrios;Username=integrios",
                })
                .Build()));
        modeException.Message.ShouldContain("Database:Postgres:Authentication");

        InvalidOperationException providerException = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminInfrastructureServices(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "sqlserver",
                    ["Database:Postgres:Authentication"] = "Password",
                    ["ConnectionStrings:SqlServer"] = "Server=localhost;Database=integrios;User Id=sa;Password=secret",
                })
                .Build()));
        providerException.Message.ShouldContain("Database:Postgres:Authentication");
    }

    [Fact]
    public void UnsupportedProvider_IsRejectedAtRegistration()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminInfrastructureServices(BuildConfiguration("sqlite")));

        exception.Message.ShouldBe("Database:Provider 'sqlite' is not supported.");
    }

    [Fact]
    public void SqlServerProvider_RegistersSqlServerModelAndAdapters()
    {
        var services = new ServiceCollection();
        services.AddAdminInfrastructureServices(BuildConfiguration("sqlserver"));
        services.AddIngestionInfrastructureServices(BuildConfiguration("sqlserver"));

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        IntegriosDbContext context = scope.ServiceProvider.GetRequiredService<IntegriosDbContext>();

        context.Database.ProviderName.ShouldBe("Microsoft.EntityFrameworkCore.SqlServer");
        context.Model.FindEntityType(typeof(Event))!
            .FindProperty(nameof(Event.Payload))!.GetColumnType().ShouldBe("nvarchar(max)");
        provider.GetRequiredService<IAuthoringLock>().ShouldBeOfType<SqlServerAuthoringLock>();
        provider.GetRequiredService<IEventAcceptance>().ShouldBeOfType<SqlServerEventAcceptance>();
        scope.ServiceProvider.GetRequiredService<IConnectorManifestStore>().ShouldBeOfType<SqlServerConnectorManifestStore>();
    }

    private static IConfiguration BuildConfiguration(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["ConnectionStrings:Postgres"] =
                    "Host=localhost;Database=integrios;Username=integrios;Password=integrios",
                ["ConnectionStrings:SqlServer"] =
                    "Server=localhost;Database=integrios;User Id=sa;Password=Integrios_Test_2026!;TrustServerCertificate=True"
            })
            .Build();
}
