using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Application.FunctionalTests;

internal static class PostgresMigrationTestHelper
{
    public static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IntegriosDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new IntegriosDbContext(options);
        await context.Database.MigrateAsync();
    }
}
