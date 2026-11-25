using System.Text.Json;
using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;
using Integrios.Ingestion.Auth;
using Integrios.Ingestion.Cli;
using Integrios.Ingestion.Endpoints;
using Integrios.Ingestion.ErrorHandling;
using Microsoft.AspNetCore.Authentication;

namespace Integrios.Ingestion;

public static class IngestionHost
{
    public const string Name = "ingestion";
    public const string DefaultUrl = "http://localhost:5231";
    public const int DefaultOperationalPort = 5232;

    public static async Task<int> RunAsync(string[] args)
    {
        bool secretCommand = SourceSecretValidationCli.IsCommand(args);

        try
        {
            (WebApplicationBuilder builder, int operationalPort) = CreateBuilder(args);
            var app = builder.Build();

            if (secretCommand)
            {
                int exitCode = await SourceSecretValidationCli.RunAsync(args, app.Services, Console.Out, Console.Error);
                await app.DisposeAsync();
                return exitCode;
            }

            ConfigurePipeline(app, operationalPort);
            await app.RunAsync();
            return 0;
        }
        catch (Exception) when (secretCommand)
        {
            Console.Error.WriteLine("Secret validation could not start with the current configuration.");
            return 2;
        }
    }

    public static void ConfigurePipeline(WebApplication app, int operationalPort)
    {
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

        app.MapEndpoints(typeof(IngestionHost).Assembly);
        app.MapOperationalEndpoints();
    }

    public static (WebApplicationBuilder Builder, int OperationalPort) CreateBuilder(string[] args)
    {
        var builder = ServiceHosting.CreateBuilder(args, Name, typeof(IngestionHost).Assembly.GetName().Name!);
        builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
        bool secretCommand = SourceSecretValidationCli.IsCommand(args);

        builder.Configuration.AddSecretConfigurationSources();
        int operationalPort = builder.AddOperationalEndpoints(DefaultOperationalPort, DefaultUrl);

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

        return (builder, operationalPort);
    }
}
