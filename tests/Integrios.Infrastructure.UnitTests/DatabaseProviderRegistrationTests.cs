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
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        var entityTypes = context.Model.GetEntityTypes().ToArray();
        string[] tables = entityTypes.Select(entity => entity.GetTableName()!).ToArray();
        Assert.Contains("tenants", tables);
        Assert.All(
            entityTypes.Where(entity => entity.ClrType != typeof(OutboxEntry)),
            entity => Assert.StartsWith("Integrios.Domain.", entity.ClrType.Namespace));

        Assert.Equal("tenants", context.Model.FindEntityType(typeof(Tenant))?.GetTableName());
        Assert.Equal("events", context.Model.FindEntityType(typeof(Event))?.GetTableName());
        Assert.Equal(
            "event_deliveries",
            context.Model.FindEntityType(typeof(EventDelivery))?.GetTableName());
        Assert.Equal(
            "delivery_attempts",
            context.Model.FindEntityType(typeof(DeliveryAttempt))?.GetTableName());
        Assert.Equal("outbox", context.Model.FindEntityType(typeof(OutboxEntry))?.GetTableName());

        var operatorKey = context.Model.FindEntityType(typeof(OperatorKey))!;
        Assert.Null(operatorKey.FindProperty("TenantId"));
        Assert.False(context.Model.FindEntityType(typeof(TopicSource))!.FindProperty("Status")!.IsNullable);
        Assert.False(context.Model.FindEntityType(typeof(SourceEndpoint))!.FindProperty("Status")!.IsNullable);

        var status = context.Model.FindEntityType(typeof(EventDelivery))!
            .FindProperty(nameof(EventDelivery.Status))!;
        Assert.Equal(
            "in_flight",
            status.GetTypeMapping().Converter!.ConvertToProvider(EventDeliveryStatus.InFlight));
        Assert.IsType<StringListValueComparer>(context.Model.FindEntityType(typeof(Connector))!
            .FindProperty(nameof(Connector.SupportedAuthSchemes))!
            .GetValueComparer());
    }

    [Fact]
    public void UnsupportedProvider_IsRejectedAtRegistration()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAdminInfrastructureServices(BuildConfiguration("sqlite")));

        Assert.Equal("Database:Provider 'sqlite' is not supported.", exception.Message);
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

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.Equal("nvarchar(max)", context.Model.FindEntityType(typeof(Event))!
            .FindProperty(nameof(Event.Payload))!.GetColumnType());
        Assert.IsType<SqlServerConnectionAuthoringLock>(provider.GetRequiredService<IConnectionAuthoringLock>());
        Assert.IsType<SqlServerEventAcceptance>(provider.GetRequiredService<IEventAcceptance>());
        Assert.IsType<SqlServerConnectorManifestStore>(scope.ServiceProvider.GetRequiredService<IConnectorManifestStore>());
    }

    [Fact]
    public void StringListValueComparer_UsesStructuralEqualityAndSnapshots()
    {
        var comparer = new StringListValueComparer();
        IReadOnlyList<string> values = ["api_key_header", "bearer_token"];

        Assert.True(comparer.Equals(values, values.ToArray()));
        Assert.False(comparer.Equals(values, ["bearer_token", "api_key_header"]));
        Assert.NotSame(values, comparer.Snapshot(values));
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
