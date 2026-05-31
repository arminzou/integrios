using System.Diagnostics.Metrics;
using Dapper;
using Integrios.Application.Telemetry;
using Integrios.Infrastructure.Data;
using Microsoft.Extensions.Hosting;

namespace Integrios.Infrastructure.Telemetry;

// Global gauge of unprocessed outbox rows. The observe callback only runs when a
// scrape collects, so it is bound by the Prometheus scrape interval and never by the
// worker loop tick. Registered as a hosted service purely to force eager construction
// so the instrument exists before the first scrape.
internal sealed class OutboxDepthMetrics : IHostedService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly Meter _meter;

    public OutboxDepthMetrics(IMeterFactory meterFactory, IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        _meter = meterFactory.Create(IntegriosMetrics.MeterName);
        _meter.CreateObservableGauge("integrios_outbox_pending_depth", ObservePendingDepth);
    }

    private long ObservePendingDepth()
    {
        using var connection = _connectionFactory.OpenConnectionAsync().AsTask().GetAwaiter().GetResult();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox WHERE processed_at IS NULL");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
