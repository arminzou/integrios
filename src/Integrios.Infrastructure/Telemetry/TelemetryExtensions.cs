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

    public static ILoggingBuilder AddOperationalConsoleLogging(
        this ILoggingBuilder logging,
        bool isDevelopment)
    {
        logging.ClearProviders();

        if (isDevelopment)
            logging.AddSimpleConsole();
        else
            logging.AddJsonConsole();

        return logging;
    }

    public static IServiceCollection AddTelemetryServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        // OTLP is off unless an endpoint is configured; spans are still produced in-process.
        var otlpEndpoint = configuration["Integrios:Telemetry:OtlpEndpoint"]
            ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
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
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddSqlClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            });

        return services;
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
        services.AddHostedService<OutboxDepthMetrics>();
        return services;
    }
}
