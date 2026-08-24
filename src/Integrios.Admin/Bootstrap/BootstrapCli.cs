using Integrios.Application;
using Integrios.Application.Bootstrap;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Admin.Bootstrap;

public static class BootstrapCli
{
    private const string GlobalOperatorPublicKey = "global_operator_key";
    private const string OperatorKeySecretConfigKey = "INTEGRIOS_BOOTSTRAP_OPERATOR_KEY_SECRET";

    // Denylist entry only: refuses this well-known dev secret when running with a Production environment.
    private const string WellKnownDevSecret = "operator_bootstrap_secret";

    public static async Task<int> RunAsync(string[] args)
    {
        string[] flags = args.Skip(1).ToArray();
        if (flags.Any(flag => flag is not ("--builtins" or "--operator-key")))
        {
            Console.Error.WriteLine("Usage: bootstrap [--builtins] [--operator-key]");
            return 2;
        }

        bool runBuiltins = flags.Length == 0 || flags.Contains("--builtins");
        bool runOperatorKey = flags.Length == 0 || flags.Contains("--operator-key");

        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddAdminApplicationServices();
        hostBuilder.Services.AddAdminInfrastructureServices(hostBuilder.Configuration);

        using IHost host = hostBuilder.Build();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        string? operatorKeySecret = runOperatorKey
            ? hostBuilder.Configuration[OperatorKeySecretConfigKey]
            : null;

        if (runOperatorKey && hostBuilder.Environment.IsProduction() && string.IsNullOrWhiteSpace(operatorKeySecret))
        {
            Console.Error.WriteLine(
                $"operator-key: Production Bootstrap requires a non-empty {OperatorKeySecretConfigKey} value.");
            return 1;
        }

        if (runOperatorKey && hostBuilder.Environment.IsProduction() && operatorKeySecret == WellKnownDevSecret)
        {
            Console.Error.WriteLine("operator-key: refusing to bootstrap Production with the well-known dev secret.");
            return 1;
        }

        if (runBuiltins)
        {
            IReadOnlyList<Connector> reconciled = await mediator.Send(new BootstrapBuiltinsCommand());
            Console.WriteLine($"builtins: reconciled {reconciled.Count} built-in connector(s).");
        }

        if (runOperatorKey)
        {
            BootstrapOperatorKeyResult result = await mediator.Send(
                new BootstrapOperatorKeyCommand(GlobalOperatorPublicKey, operatorKeySecret));

            if (!result.Created)
                Console.WriteLine("operator-key: a live deployment-wide operator key already exists, no-op.");
            else if (result.GeneratedSecret is not null)
                Console.WriteLine($"operator-key: created deployment-wide operator key. Secret (store securely, shown once): {result.GeneratedSecret}");
            else
                Console.WriteLine("operator-key: created deployment-wide operator key using the supplied secret.");
        }

        return 0;
    }
}
