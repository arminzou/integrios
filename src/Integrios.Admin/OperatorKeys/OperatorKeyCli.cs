using Integrios.Application;
using Integrios.Application.Bootstrap;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Admin.OperatorKeys;

public static class OperatorKeyCli
{
    internal const string RotationSecretConfigKey = "INTEGRIOS_OPERATOR_KEY_ROTATION_SECRET";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["operator-key", "rotate"])
        {
            Console.Error.WriteLine("Usage: operator-key rotate");
            return 2;
        }

        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        string? secret = hostBuilder.Configuration[RotationSecretConfigKey];
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine($"operator-key rotate: a non-empty {RotationSecretConfigKey} value is required.");
            return 2;
        }

        hostBuilder.Services.AddAdminApplicationServices();
        hostBuilder.Services.AddAdminInfrastructureServices(hostBuilder.Configuration);

        using IHost host = hostBuilder.Build();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        RotateOperatorKeyResult result;
        try
        {
            result = await mediator.Send(new RotateOperatorKeyCommand(secret));
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"operator-key rotate: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"operator-key: rotated deployment-wide key. Public key: {result.PublicKey}");
        return 0;
    }
}
