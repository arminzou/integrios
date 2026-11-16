using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;
using Integrios.Worker.Cli;

namespace Integrios.Worker;

public static class WorkerRole
{
    public static async Task<int> RunAsync(string[] args)
    {
        bool secretCommand = SecretValidationCli.IsCommand(args);

        try
        {
            (WebApplicationBuilder builder, int operationalPort) = CreateBuilder(args);
            var app = builder.Build();

            if (secretCommand)
            {
                int exitCode = await SecretValidationCli.RunAsync(args, app.Services, Console.Out, Console.Error);
                await app.DisposeAsync();
                return exitCode;
            }

            app.UseRouting();
            app.UseOperationalEndpointIsolation(operationalPort);
            app.MapOperationalEndpoints();

            await app.RunAsync();
            return 0;
        }
        catch (Exception) when (secretCommand)
        {
            Console.Error.WriteLine("Secret validation could not start with the current configuration.");
            return 2;
        }
    }

    public static (WebApplicationBuilder Builder, int OperationalPort) CreateBuilder(string[] args)
    {
        var builder = ServiceHosting.CreateBuilder(args, typeof(WorkerRole).Assembly.GetName().Name!);
        builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
        bool secretCommand = SecretValidationCli.IsCommand(args);

        builder.Configuration.AddSecretConfigurationSources();
        int operationalPort = builder.AddOperationalEndpoints("WorkerMetricsPort", operationalOnly: true);

        builder.Services.AddWorkerApplicationServices();
        builder.Services.AddWorkerInfrastructureServices(builder.Configuration);
        builder.Services.AddDestinationAuthenticationSecretResolutionServices(builder.Configuration);
        builder.Services.AddTelemetryServices(builder.Configuration, "integrios-worker");
        builder.Services.AddOutboxDepthMetricsServices(builder.Configuration);

        builder.Services.AddWorkerHostServices(builder.Configuration, enableBackgroundLoops: !secretCommand);

        return (builder, operationalPort);
    }
}
