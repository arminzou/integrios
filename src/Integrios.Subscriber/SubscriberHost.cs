using System.Text.Json;
using Integrios.Application;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Hosting;
using Integrios.Infrastructure.Telemetry;
using Integrios.Subscriber.Auth;
using Integrios.Subscriber.Endpoints;
using Microsoft.AspNetCore.Authentication;

namespace Integrios.Subscriber;

public static class SubscriberHost
{
    public const string Name = "subscriber";
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
            // exception would let any key holder fill the error log. A Subscriber that disconnects
            // mid long-poll cancels the request; SqlClient reports that as a SqlException rather
            // than a cancellation, and nobody is left to answer.
            SuppressDiagnosticsCallback = context => context.Exception is BadHttpRequestException
                || context.HttpContext.RequestAborted.IsCancellationRequested
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
        var builder = ServiceHosting.CreateBuilder(args, Name, typeof(SubscriberHost).Assembly.GetName().Name!);
        builder.Logging.AddOperationalConsoleLogging(builder.Environment.IsDevelopment());
        int operationalPort = builder.AddOperationalEndpoints(DefaultOperationalPort, DefaultUrl);

        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddSubscriberApplicationServices();
        builder.Services.AddAuthentication(SubscriberKeyAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SubscriberKeyAuthHandler>(SubscriberKeyAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSubscriberInfrastructureServices(builder.Configuration);
        builder.Services.AddTelemetryServices(builder.Configuration, "integrios-subscriber");

        return (builder, operationalPort);
    }
}
