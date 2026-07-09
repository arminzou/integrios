using Integrios.Application;
using Integrios.Application.Bootstrap;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Admin.Bootstrap;

public static class BootstrapCli
{
    private const string GlobalAdminPublicKey = "global_admin_key";
    private const string AdminSecretConfigKey = "INTEGRIOS_BOOTSTRAP_ADMIN_SECRET";

    // Denylist entry only: refuses this well-known dev secret when running with a Production environment.
    private const string WellKnownDevSecret = "admin_bootstrap_secret";

    public static async Task<int> RunAsync(string[] args)
    {
        string[] flags = args.Skip(1).ToArray();
        if (flags.Any(flag => flag is not ("--builtins" or "--admin-key")))
        {
            Console.Error.WriteLine("Usage: bootstrap [--builtins] [--admin-key]");
            return 1;
        }

        bool runBuiltins = flags.Length == 0 || flags.Contains("--builtins");
        bool runAdminKey = flags.Length == 0 || flags.Contains("--admin-key");

        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddIntegriosApplication();
        hostBuilder.Services.AddIntegriosInfrastructure(hostBuilder.Configuration);

        using IHost host = hostBuilder.Build();
        IMediator mediator = host.Services.GetRequiredService<IMediator>();

        if (runBuiltins)
        {
            IReadOnlyList<Integration> reconciled = await mediator.Send(new BootstrapBuiltinsCommand());
            Console.WriteLine($"builtins: reconciled {reconciled.Count} built-in integration(s).");
        }

        if (runAdminKey)
        {
            string? secret = hostBuilder.Configuration[AdminSecretConfigKey];

            if (hostBuilder.Environment.IsProduction() && secret == WellKnownDevSecret)
            {
                Console.Error.WriteLine("admin-key: refusing to bootstrap Production with the well-known dev secret.");
                return 1;
            }

            BootstrapAdminKeyResult result = await mediator.Send(
                new BootstrapAdminKeyCommand(GlobalAdminPublicKey, secret));

            if (!result.Created)
                Console.WriteLine("admin-key: a live global admin key already exists, no-op.");
            else if (result.GeneratedSecret is not null)
                Console.WriteLine($"admin-key: created global admin key. Secret (store securely, shown once): {result.GeneratedSecret}");
            else
                Console.WriteLine("admin-key: created global admin key using the supplied secret.");
        }

        return 0;
    }
}
