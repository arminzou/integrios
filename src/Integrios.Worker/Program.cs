using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Integrios.Worker;

// The Worker is a WebApplication only to serve an operational HTTP surface
// (Prometheus /metrics now, /healthz later). The outbox loop is a hosted service.
var builder = WebApplication.CreateBuilder(args);
bool secretCommand = SecretValidationCli.IsCommand(args);

try
{
    var metricsPort = builder.Configuration.GetValue("WorkerMetricsPort", 5299);
    builder.WebHost.UseUrls($"http://0.0.0.0:{metricsPort}");

    builder.Services.AddWorkerApplicationServices();
    builder.Services.AddWorkerInfrastructureServices(builder.Configuration);
    builder.Services.AddSecretResolutionServices(builder.Configuration);
    builder.Services.AddTelemetryServices(builder.Configuration, "integrios-worker");
    builder.Services.AddOutboxDepthMetricsServices(builder.Configuration);

    builder.Services.AddWorkerHostServices(builder.Configuration, enableBackgroundLoops: !secretCommand);

    var app = builder.Build();

    if (secretCommand)
    {
        int exitCode = await SecretValidationCli.RunAsync(args, app.Services, Console.Out, Console.Error);
        await app.DisposeAsync();
        return exitCode;
    }

    app.MapPrometheusScrapingEndpoint();

    app.Run();
    return 0;
}
catch (Exception) when (secretCommand)
{
    Console.Error.WriteLine("Secret validation could not start with the current configuration.");
    return 2;
}
