using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;
using Integrios.Worker;

// The Worker is a WebApplication only to serve its operational HTTP surface.
// The outbox loop is a hosted service.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
bool secretCommand = SecretValidationCli.IsCommand(args);

try
{
    int operationalPort = builder.AddOperationalEndpoints("WorkerMetricsPort", operationalOnly: true);

    builder.Services.AddWorkerApplicationServices();
    builder.Services.AddWorkerInfrastructureServices(builder.Configuration);
    builder.Services.AddDestinationAuthenticationSecretResolutionServices(builder.Configuration);
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

    app.UseRouting();
    app.UseOperationalEndpointIsolation(operationalPort);
    app.MapOperationalEndpoints();

    app.Run();
    return 0;
}
catch (Exception) when (secretCommand)
{
    Console.Error.WriteLine("Secret validation could not start with the current configuration.");
    return 2;
}
