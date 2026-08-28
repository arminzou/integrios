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
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
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
        services.AddHostedService<OutboxDepthMetrics>();
        return services;
    }
}
