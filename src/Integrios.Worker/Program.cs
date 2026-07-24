using Integrios.Application;
using Integrios.Application.Delivery;
using Integrios.Infrastructure;
using Integrios.Infrastructure.Telemetry;
using Integrios.Worker;

// The Worker is a WebApplication only to serve an operational HTTP surface
// (Prometheus /metrics now, /healthz later). The outbox loop is a hosted service.
var builder = WebApplication.CreateBuilder(args);

var metricsPort = builder.Configuration.GetValue("WorkerMetricsPort", 5299);
builder.WebHost.UseUrls($"http://0.0.0.0:{metricsPort}");

builder.Services.AddIntegriosApplication();
builder.Services.AddIntegriosInfrastructure(builder.Configuration);
builder.Services.AddIntegriosTelemetry(builder.Configuration, "integrios-worker");

// Only the Worker holds in-flight delivery attempts at shutdown; the shutdown
// timeout must outlast the attempt deadline so finalization can commit.
builder.Services.AddOptions<HostOptions>()
    .Configure<DeliveryExecutionOptions>((hostOptions, deliveryOptions) =>
        hostOptions.ShutdownTimeout = deliveryOptions.ShutdownGracePeriod);

builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();

app.MapPrometheusScrapingEndpoint();

app.Run();
