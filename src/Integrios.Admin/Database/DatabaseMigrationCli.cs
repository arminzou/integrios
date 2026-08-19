using Integrios.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace Integrios.Admin.Database;

public static class DatabaseMigrationCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["database", "migrate"] and not ["database", "info"])
        {
            Console.Error.WriteLine("Usage: database <migrate|info>");
            return 2;
        }

        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddAdminInfrastructureServices(hostBuilder.Configuration);

        using IHost host = hostBuilder.Build();
        if (args[1] == "migrate")
        {
            await host.Services.MigrateDatabaseAsync();
            Console.WriteLine("database: migrations applied.");
        }
        else
        {
            Console.WriteLine(await host.Services.GetDatabaseMigrationInfoAsync());
        }

        return 0;
    }
}
