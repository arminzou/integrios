using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Integrios.Infrastructure.Data;

internal sealed class IntegriosDbContextFactory : IDesignTimeDbContextFactory<IntegriosDbContext>
{
    public IntegriosDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IntegriosDbContext>()
            .UseNpgsql("Host=localhost;Database=integrios")
            .Options;

        return new IntegriosDbContext(options);
    }
}
