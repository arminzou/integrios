using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Data;

internal static class IntegriosDbContextOptions
{
    // One definition of how a provider is configured, so a test harness cannot drift from what
    // the composition root registers. Transient faults are routine on hosted databases, so both
    // providers retry; without this the first pre-login handshake failure fails the operation.
    public static DbContextOptionsBuilder UseIntegriosProvider(
        this DbContextOptionsBuilder builder,
        DatabaseProvider provider,
        string connectionString) =>
        provider == DatabaseProvider.SqlServer
            ? builder.UseSqlServer(
                connectionString,
                sql => sql
                    .MigrationsAssembly("Integrios.Migrations.SqlServer")
                    .EnableRetryOnFailure())
            : builder.UseNpgsql(
                connectionString,
                postgres => postgres
                    .MigrationsAssembly("Integrios.Migrations.Postgres")
                    .EnableRetryOnFailure());
}
