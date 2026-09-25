using Integrios.Infrastructure;
using Integrios.Infrastructure.Data;
using Microsoft.Extensions.Hosting;

namespace Integrios.Admin.Database;

public static class DatabaseMigrationCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["database", "--help"] or ["database", "grant-runtime", "--help"])
        {
            Console.WriteLine(
                "Usage: database <migrate|info|grant-runtime>\n"
                + "grant-runtime reads Database:RuntimePrincipals from .NET configuration.\n"
                + "A supplied Password is set on every run. Omit credential keys for grant-only mode.\n"
                + "Do not pass secrets as command-line configuration; use environment variables or a secret provider.");
            return 0;
        }

        if (args is not ["database", "migrate"]
            and not ["database", "info"]
            and not ["database", "grant-runtime"])
        {
            Console.Error.WriteLine("Usage: database <migrate|info|grant-runtime>");
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
        else if (args[1] == "grant-runtime")
        {
            await host.Services.GrantRuntimePrincipalsAsync(hostBuilder.Configuration);
            Console.WriteLine("database: runtime principal scopes applied.");
        }
        else
        {
            Console.WriteLine(await host.Services.GetDatabaseMigrationInfoAsync());
        }

        return 0;
    }
}
