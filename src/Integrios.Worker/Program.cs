using Integrios.Application;
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

builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();

app.MapPrometheusScrapingEndpoint();

app.Run();
