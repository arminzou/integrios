using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Integrios.Migrations.SqlServer;

internal sealed class SqlServerDbContextFactory : IDesignTimeDbContextFactory<IntegriosDbContext>
{
    public IntegriosDbContext CreateDbContext(string[] args)
    {
        string connectionString = args.FirstOrDefault()
            ?? "Server=localhost;Database=integrios;User Id=sa;Password=Integrios_dev_123!;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<IntegriosDbContext>()
            .UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsAssembly(typeof(SqlServerDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new IntegriosDbContext(options);
    }
}
