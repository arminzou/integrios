using System.Data.Common;
using Dapper;
using Integrios.Infrastructure.Data;
using Integrios.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integrios.FunctionalTests.Infrastructure;

// Before the built-in subscriber Connector existed, `subscriber` was an ordinary authorable key. A
// deployment holding such a Connector must stop at the seeding migration instead of having that
// Connector silently become pull.
public sealed class SubscriberConnectorSeedTests : IAsyncLifetime
{
    private readonly FunctionalDatabase database = new(migrateOnStart: false);

    public Task InitializeAsync() => database.StartAsync();

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task Seeding_RefusesAnAuthoredSubscriberConnectorAndLeavesItUntouched()
    {
        await using var context = new IntegriosDbContext(database.CreateOptions());
        IMigrator migrator = context.GetService<IMigrator>();
        List<string> migrations = context.Database.GetMigrations().ToList();
        int seed = migrations.FindIndex(name => name.EndsWith("_SeedSubscriberConnector", StringComparison.Ordinal));
        await migrator.MigrateAsync(migrations[seed - 1]);

        Guid authoredId = Guid.NewGuid();
        await using DbConnection connection = database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            INSERT INTO connectors (id,{database.KeyColumn},contract_version,manifest_schema_version,name,direction,manifest,created_at,updated_at)
            VALUES (@Id,'subscriber',2,1,'Authored','destination',{database.Json("@Manifest")},{database.Now},{database.Now})
            """,
            new
            {
                Id = authoredId,
                Manifest = TestConnectorManifest.Create("subscriber", "Authored", "destination")
                    .Replace("\"contract_version\":1", "\"contract_version\":2", StringComparison.Ordinal),
            });

        Exception refused = await Should.ThrowAsync<Exception>(() => migrator.MigrateAsync());

        refused.Message.ShouldContain("reserved for the built-in pull Connector");
        (await connection.QuerySingleAsync<Guid>("SELECT id FROM connectors")).ShouldBe(authoredId);
    }
}
