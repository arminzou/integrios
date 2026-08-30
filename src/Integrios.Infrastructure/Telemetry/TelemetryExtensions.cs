using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Integrios.Infrastructure.Telemetry;

public static class TelemetryExtensions
{
    private static readonly TimeSpan DefaultOutboxDepthSampleInterval = TimeSpan.FromSeconds(15);
    private const string OtlpEndpointConfigurationKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static ILoggingBuilder AddOperationalConsoleLogging(
        this ILoggingBuilder logging,
        bool isDevelopment)
    {
        logging.ClearProviders();
        logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        if (isDevelopment)
            logging.AddSimpleConsole(options => options.IncludeScopes = true);
        else
            logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
            });

        return logging;
    }

    public static IApplicationBuilder UseRequestCompletionLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestCompletionLoggingMiddleware>();

    public static IServiceCollection AddTelemetryServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        bool exportTraces = HasValidOtlpEndpoint(configuration);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName,
                serviceVersion: typeof(TelemetryExtensions).Assembly.GetName().Version!.ToString(3),
                serviceInstanceId: Guid.NewGuid().ToString("N")))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("integrios.application")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddPrometheusExporter();
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("integrios.application")
                    .AddAspNetCoreInstrumentation(options => options.Filter =
                        context => !RequestCompletionLoggingMiddleware.IsOperationalRequest(context.Request.Path))
                    .AddHttpClientInstrumentation(options =>
                        options.EnrichWithHttpRequestMessage = static (activity, _) =>
                        {
                            activity.SetTag("url.full", null);
                            activity.SetTag("http.url", null);
                        })
                    .AddNpgsql()
                    .AddSqlClientInstrumentation();

                if (exportTraces)
                {
                    tracing.AddOtlpExporter();
                }
            });

        return services;
    }

    private static bool HasValidOtlpEndpoint(IConfiguration configuration)
    {
        string? endpoint = configuration[OtlpEndpointConfigurationKey];
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{OtlpEndpointConfigurationKey} must be an absolute HTTP(S) URI.");
        }

        return true;
    }

    public static IServiceCollection AddOutboxDepthMetricsServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string? configuredInterval = configuration["Integrios:Telemetry:OutboxDepthSampleInterval"];
        TimeSpan sampleInterval = string.IsNullOrWhiteSpace(configuredInterval)
            ? DefaultOutboxDepthSampleInterval
            : TimeSpan.TryParse(configuredInterval, out TimeSpan parsed)
                ? parsed
                : throw new InvalidOperationException(
                    "Integrios:Telemetry:OutboxDepthSampleInterval must be a TimeSpan value.");

        if (sampleInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Integrios:Telemetry:OutboxDepthSampleInterval must be positive.");
        }

        services.AddSingleton(new OutboxDepthMetricsOptions(sampleInterval));
        services.AddSingleton<BacklogSnapshotReader>();
        services.AddHostedService<OutboxDepthMetrics>();
        return services;
    }
}

internal sealed class RequestCompletionLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestCompletionLoggingMiddleware> logger)
{
    private static readonly EventId RequestCompleted = new(1000, "HttpRequestCompleted");

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsOperationalRequest(context.Request.Path))
        {
            await next(context);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        await next(context);

        string routeTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        logger.LogInformation(
            RequestCompleted,
            "Completed HTTP request {method} {route_template} with {status} in {duration_ms} ms (request_id={request_id}).",
            context.Request.Method,
            routeTemplate,
            context.Response.StatusCode,
            stopwatch.Elapsed.TotalMilliseconds,
            context.TraceIdentifier);
    }

    internal static bool IsOperationalRequest(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/ready", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);
}
