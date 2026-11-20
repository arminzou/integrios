using System.Data.Common;
using System.Text.Json;
using Dapper;
using Integrios.Infrastructure.Data;
using Integrios.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integrios.FunctionalTests.Infrastructure;

public sealed class PullConnectorRenameTests : IAsyncLifetime
{
    private readonly FunctionalDatabase database = new(migrateOnStart: false);
    private IntegriosDbContext? context;
    private DbConnection? connection;

    public Task InitializeAsync() => database.StartAsync();

    public async Task DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
        if (context is not null)
            await context.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task Upgrade_RenamesTheSeededSubscriberConnectorToPullInPlace()
    {
        IMigrator migrator = await MigrateToBeforeRenameAsync();

        await migrator.MigrateAsync();

        var row = await connection!.QuerySingleAsync<(string Key, string Name, string Manifest)>(
            $"SELECT {database.KeyColumn}, name, {database.JsonText("manifest")} FROM connectors WHERE id = @Id",
            new { Id = Guid.Parse("5ab5c71b-e000-4000-8000-000000000001") });
        row.Key.ShouldBe("pull");
        row.Name.ShouldBe("Pull");
        using JsonDocument manifest = JsonDocument.Parse(row.Manifest);
        manifest.RootElement.GetProperty("key").GetString().ShouldBe("pull");
        manifest.RootElement.GetProperty("presentation").GetProperty("name").GetString().ShouldBe("Pull");
    }

    [Fact]
    public async Task Upgrade_RefusesAnAuthoredPullConnectorAndLeavesItUntouched()
    {
        IMigrator migrator = await MigrateToBeforeRenameAsync();
        Guid authoredId = Guid.NewGuid();
        await connection!.ExecuteAsync(
            $"""
            INSERT INTO connectors (id,{database.KeyColumn},contract_version,manifest_schema_version,name,direction,manifest,created_at,updated_at)
            VALUES (@Id,'pull',1,1,'Authored','destination',{database.Json("@Manifest")},{database.Now},{database.Now})
            """,
            new { Id = authoredId, Manifest = TestConnectorManifest.Create("pull", "Authored", "destination") });

        Exception refused = await Should.ThrowAsync<Exception>(() => migrator.MigrateAsync());

        refused.Message.ShouldContain("reserved for the built-in pull Connector");
        (await connection!.QuerySingleAsync<string>(
            $"SELECT name FROM connectors WHERE {database.KeyColumn} = 'pull'")).ShouldBe("Authored");
        (await connection!.QuerySingleAsync<string>(
            $"SELECT {database.KeyColumn} FROM connectors WHERE id = '5ab5c71b-e000-4000-8000-000000000001'")).ShouldBe("subscriber");
    }

    private async Task<IMigrator> MigrateToBeforeRenameAsync()
    {
        context = new IntegriosDbContext(database.CreateOptions());
        IMigrator migrator = context.GetService<IMigrator>();
        List<string> migrations = context.Database.GetMigrations().ToList();
        int rename = migrations.FindIndex(name => name.EndsWith("_RenameSubscriberConnectorToPull", StringComparison.Ordinal));
        await migrator.MigrateAsync(migrations[rename - 1]);

        connection = database.CreateConnection();
        await connection.OpenAsync();
        return migrator;
    }
}
