using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Integrios.Infrastructure.Outbox;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Ingestion;
using Integrios.Application.Authoring.Connectors;
using Integrios.Infrastructure.Connections;
using Integrios.Infrastructure.Events;
using Integrios.Infrastructure.Connectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        context.Model.FindEntityType(typeof(TopicSource))!.FindProperty("Status")!.IsNullable.ShouldBeFalse();
        context.Model.FindEntityType(typeof(SourceEndpoint))!.FindProperty("Status")!.IsNullable.ShouldBeFalse();

        var status = context.Model.FindEntityType(typeof(EventDelivery))!
            .FindProperty(nameof(EventDelivery.Status))!;
        status.GetTypeMapping().Converter!.ConvertToProvider(EventDeliveryStatus.InFlight).ShouldBe(
            "in_flight");
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
        provider.GetRequiredService<IConnectionAuthoringLock>().ShouldBeOfType<SqlServerConnectionAuthoringLock>();
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
