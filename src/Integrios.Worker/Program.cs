using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Worker;

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
