using System.Text.Json;
using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;
using Integrios.Ingestion;
using Integrios.Ingestion.Auth;
using Integrios.Ingestion.Cli;
using Integrios.Ingestion.Endpoints;
using Integrios.Ingestion.ErrorHandling;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
bool secretCommand = SourceSecretValidationCli.IsCommand(args);

try
{
    builder.Configuration.AddSecretConfigurationSources();
    int operationalPort = builder.AddOperationalEndpoints("OperationalPort");

    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

    builder.Services.AddOpenApi();
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<IngestionExceptionHandler>();
    builder.Services.AddIngestionApplicationServices();
    builder.Services.AddIngestionInfrastructureServices(builder.Configuration, enableBrokerReceiver: !secretCommand);
    builder.Services.AddSourceVerificationSecretResolutionServices(builder.Configuration);
    builder.Services.AddTelemetryServices(builder.Configuration, "integrios-ingestion");

    builder.Services.AddAuthentication(TenantApiKeyAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, TenantApiKeyAuthHandler>(TenantApiKeyAuthHandler.SchemeName, _ => { });
    builder.Services.AddAuthorization();

    var app = builder.Build();

    if (secretCommand)
    {
        int exitCode = await SourceSecretValidationCli.RunAsync(args, app.Services, Console.Out, Console.Error);
        await app.DisposeAsync();
        return exitCode;
    }

    app.UseRouting();
    app.UseOperationalEndpointIsolation(operationalPort);
    app.UseRequestCompletionLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();

    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapEndpoints(typeof(Program).Assembly);
    app.MapOperationalEndpoints();

    app.Run();
    return 0;
}
catch (Exception) when (secretCommand)
{
    Console.Error.WriteLine("Secret validation could not start with the current configuration.");
    return 2;
}
