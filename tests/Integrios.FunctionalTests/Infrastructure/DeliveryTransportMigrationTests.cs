using System.Data.Common;
using Dapper;
using Integrios.Infrastructure.Data;
using Integrios.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integrios.FunctionalTests.Infrastructure;

public sealed class DeliveryTransportMigrationTests : IAsyncLifetime
{
    private readonly FunctionalDatabase database = new(migrateOnStart: false);

    public Task InitializeAsync() => database.StartAsync();

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task Upgrade_RefusesToReinterpretAnAuthoredSubscriberConnector()
    {
        await using var context = new IntegriosDbContext(database.CreateOptions());
        IMigrator migrator = context.GetService<IMigrator>();
        List<string> migrations = context.Database.GetMigrations().ToList();
        int transport = migrations.FindIndex(name => name.EndsWith("_AddEventDeliveryTransport", StringComparison.Ordinal));
        await migrator.MigrateAsync(migrations[transport - 1]);

        Guid authoredId = Guid.NewGuid();
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            INSERT INTO connectors (id,{database.KeyColumn},contract_version,manifest_schema_version,name,direction,manifest,created_at,updated_at)
            VALUES (@Id,'subscriber',1,1,'Authored','destination',{database.Json("@Manifest")},{database.Now},{database.Now})
            """,
            new
            {
                Id = authoredId,
                Manifest = TestConnectorManifest.Create("subscriber", "Authored", "destination")
            });

        Exception refused = await Should.ThrowAsync<Exception>(() => migrator.MigrateAsync(migrations[transport]));

        refused.Message.ShouldContain("reserved for the built-in pull Connector");
        (await connection.QuerySingleAsync<Guid>("SELECT id FROM connectors")).ShouldBe(authoredId);
        (await context.Database.GetAppliedMigrationsAsync()).ShouldNotContain(migrations[transport]);
    }
}
