using Integrios.Application;
using Integrios.Application.Bootstrap;
using Integrios.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Integrios.Admin.AdminKeys;

public static class AdminKeyCli
{
    internal const string RotationSecretConfigKey = "INTEGRIOS_ADMIN_KEY_ROTATION_SECRET";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["admin-key", "rotate"])
        {
            Console.Error.WriteLine("Usage: admin-key rotate");
            return 2;
        }

        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        string? secret = hostBuilder.Configuration[RotationSecretConfigKey];
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine($"admin-key rotate: a non-empty {RotationSecretConfigKey} value is required.");
            return 2;
        }

        hostBuilder.Services.AddAdminApplicationServices();
        hostBuilder.Services.AddAdminInfrastructureServices(hostBuilder.Configuration);

        using IHost host = hostBuilder.Build();
        IMediator mediator = host.Services.GetRequiredService<IMediator>();
        RotateAdminKeyResult result;
        try
        {
            result = await mediator.Send(new RotateAdminKeyCommand(secret));
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"admin-key rotate: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"admin-key: rotated deployment-wide key. Public key: {result.PublicKey}");
        return 0;
    }
}
