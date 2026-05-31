using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Integrios.Infrastructure.Telemetry;

public static class TelemetryExtensions
{
    public static IServiceCollection AddIntegriosTelemetry(
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
                    .AddNpgsql();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithLogging();

        // IncludeFormattedMessage/IncludeScopes must be set on OpenTelemetryLoggerOptions, not on LoggerProviderBuilder, configure separately after WithLogging registers the provider.
        services.Configure<OpenTelemetryLoggerOptions>(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });

        return services;
    }
}
