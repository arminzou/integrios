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
    private const string DevAdminSecret = "admin_bootstrap_secret";
    private const string AdminSecretConfigKey = "INTEGRIOS_BOOTSTRAP_ADMIN_SECRET";

    public static async Task<int> RunAsync(string[] args)
    {
        string? verb = args.Length > 1 ? args[1] : null;
        if (verb is not ("builtins" or "admin-key" or "dev"))
        {
            Console.Error.WriteLine("Usage: bootstrap <builtins|admin-key|dev>");
            return 1;
        }

        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddIntegriosApplication();
        hostBuilder.Services.AddIntegriosInfrastructure(hostBuilder.Configuration);

        using IHost host = hostBuilder.Build();
        IMediator mediator = host.Services.GetRequiredService<IMediator>();

        if (verb is "builtins" or "dev")
        {
            IReadOnlyList<Integration> reconciled = await mediator.Send(new BootstrapBuiltinsCommand());
            Console.WriteLine($"builtins: reconciled {reconciled.Count} built-in integration(s).");
        }

        if (verb is "admin-key" or "dev")
        {
            string? secret = verb == "dev" ? DevAdminSecret : hostBuilder.Configuration[AdminSecretConfigKey];

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
