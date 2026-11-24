using Integrios.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace Integrios.Admin.Cli;

public static class SetupCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["setup", "--help"])
        {
            Console.WriteLine(
                "Usage: setup\n"
                + "Applies migrations, grants Database:RuntimePrincipals, then creates the first OperatorKey if none is live.\n"
                + "Runs as the database owner and is safe to re-run; each step converges on the existing state.");
            return 0;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: setup");
            return 2;
        }

        using IHost host = BootstrapCli.BuildHost(out HostApplicationBuilder hostBuilder);
        // Checked before any step runs, so a missing Production secret changes nothing.
        if (!BootstrapCli.TryReadOperatorKeySecret(hostBuilder, out string? operatorKeySecret))
            return 1;

        await host.Services.MigrateDatabaseAsync();
        Console.WriteLine("database: migrations applied.");
        await host.Services.GrantRuntimePrincipalsAsync(hostBuilder.Configuration);
        Console.WriteLine("database: runtime principal scopes applied.");
        await BootstrapCli.BootstrapOperatorKeyAsync(host.Services, operatorKeySecret);
        return 0;
    }
}
