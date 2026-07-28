using System.Diagnostics.Metrics;
using Dapper;
using Integrios.Application.Telemetry;
using Integrios.Infrastructure.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integrios.Infrastructure.Telemetry;

// Worker-owned global gauge of unprocessed outbox rows. Database sampling happens
// asynchronously in the background; metric collection only reads the cached value.
internal sealed class OutboxDepthMetrics : BackgroundService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly OutboxDepthMetricsOptions _options;
    private readonly ILogger<OutboxDepthMetrics> _logger;
    private readonly Meter _meter;
    private long _pendingDepth;
    private int _hasMeasurement;

    public OutboxDepthMetrics(
        IMeterFactory meterFactory,
        IDbConnectionFactory connectionFactory,
        OutboxDepthMetricsOptions options,
        ILogger<OutboxDepthMetrics> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
        _meter = meterFactory.Create(IntegriosMetrics.MeterName);
        _meter.CreateObservableGauge("integrios_outbox_pending_depth", ObservePendingDepth);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SamplePendingDepthAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.SampleInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SamplePendingDepthAsync(stoppingToken);
        }
    }

    private IEnumerable<Measurement<long>> ObservePendingDepth()
    {
        if (Volatile.Read(ref _hasMeasurement) == 0)
        {
            return [];
        }

        return [new Measurement<long>(Interlocked.Read(ref _pendingDepth))];
    }

    private async Task SamplePendingDepthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            long pendingDepth = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM outbox WHERE processed_at IS NULL",
                cancellationToken: cancellationToken));

            Interlocked.Exchange(ref _pendingDepth, pendingDepth);
            Volatile.Write(ref _hasMeasurement, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not sample the pending outbox depth; retaining the last successful value.");
        }
    }
}

internal sealed record OutboxDepthMetricsOptions(TimeSpan SampleInterval);
