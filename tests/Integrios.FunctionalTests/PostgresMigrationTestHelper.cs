using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.FunctionalTests;

internal static class PostgresMigrationTestHelper
{
    public static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IntegriosDbContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsAssembly("Integrios.Migrations.Postgres"))
            .Options;
        await using var context = new IntegriosDbContext(options);
        await context.Database.MigrateAsync();
    }
}
