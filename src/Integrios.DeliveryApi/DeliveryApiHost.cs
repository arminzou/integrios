using System.Text.Json;
using Integrios.Application;
using Integrios.DeliveryApi.Auth;
using Integrios.DeliveryApi.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;

namespace Integrios.DeliveryApi;

public static class DeliveryApiHost
{
    public const string Name = "delivery-api";
    // Loopback unless the operator configures a listener, because Subscribers on the same server
    // are the default; remote Subscribers need an explicit, reviewed exposure.
    public const string DefaultUrl = "http://localhost:5241";
    public const int DefaultOperationalPort = 5242;

    public static async Task<int> RunAsync(string[] args)
    {
        (WebApplicationBuilder builder, int operationalPort) = CreateBuilder(args);
        var app = builder.Build();
        ConfigurePipeline(app, operationalPort);
        await app.RunAsync();
        return 0;
    }

    public static void ConfigurePipeline(WebApplication app, int operationalPort)
    {
        app.UseRouting();
        app.UseOperationalEndpointIsolation(operationalPort);
        app.UseRequestCompletionLogging();
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError,
            // An unreadable body is the caller's error, answered with 400; logging it as an unhandled
            // exception would let any key holder fill the error log.
            SuppressDiagnosticsCallback = context => context.Exception is BadHttpRequestException
        });
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDeliveryEndpoints();
        app.MapOperationalEndpoints();
    }

    public static (WebApplicationBuilder Builder, int OperationalPort) CreateBuilder(string[] args)
    {
        var builder = ServiceHosting.CreateBuilder(args, Name, typeof(DeliveryApiHost).Assembly.GetName().Name!);
        builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
        int operationalPort = builder.AddOperationalEndpoints(DefaultOperationalPort, defaultProductUrl: DefaultUrl);

        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddDeliveryApiApplicationServices();
        builder.Services.AddAuthentication(SubscriberKeyAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SubscriberKeyAuthHandler>(SubscriberKeyAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddDeliveryApiInfrastructureServices(builder.Configuration);
        builder.Services.AddTelemetryServices(builder.Configuration, "integrios-delivery-api");

        return (builder, operationalPort);
    }
}
