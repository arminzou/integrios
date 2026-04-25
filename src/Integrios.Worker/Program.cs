using Integrios.Worker;
using Integrios.Application;
using Integrios.Infrastructure.Extensions;

namespace Integrios.Worker;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddIntegriosApplication();
        builder.Services.AddIntegriosInfrastructure(builder.Configuration);

        builder.Services.AddHostedService<OutboxWorker>();

        var host = builder.Build();
        host.Run();
    }
}
