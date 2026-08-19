using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Integrios.Migrations.Postgres;

internal sealed class PostgresDbContextFactory : IDesignTimeDbContextFactory<IntegriosDbContext>
{
    public IntegriosDbContext CreateDbContext(string[] args)
    {
        string connectionString = args.FirstOrDefault() ?? "Host=localhost;Database=integrios";
        var options = new DbContextOptionsBuilder<IntegriosDbContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsAssembly(
                    typeof(PostgresDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new IntegriosDbContext(options);
    }
}
