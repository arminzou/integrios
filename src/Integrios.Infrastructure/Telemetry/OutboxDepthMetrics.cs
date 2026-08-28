using System.Diagnostics.Metrics;
using System.Diagnostics;
using Integrios.Application.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integrios.Infrastructure.Telemetry;

// Worker-owned backlog gauges. Database sampling happens asynchronously in the
// background; metric collection only reads the cached snapshot.
internal sealed class OutboxDepthMetrics : BackgroundService
{
    private readonly BacklogSnapshotReader _snapshotReader;
    private readonly OutboxDepthMetricsOptions _options;
    private readonly ILogger<OutboxDepthMetrics> _logger;
    private readonly Meter _meter;
    private BacklogSnapshot? _snapshot;
    private long _lastSuccessfulSampleTimestamp;
    private int _hasMeasurement;

    public OutboxDepthMetrics(
        IMeterFactory meterFactory,
        BacklogSnapshotReader snapshotReader,
        OutboxDepthMetricsOptions options,
        ILogger<OutboxDepthMetrics> logger)
    {
        _snapshotReader = snapshotReader;
        _options = options;
        _logger = logger;
        _meter = meterFactory.Create(IntegriosMetrics.MeterName);
        _meter.CreateObservableGauge("integrios_outbox_pending_depth", ObservePendingDepth);
        _meter.CreateObservableGauge("integrios_outbox_oldest_pending_age_seconds", ObserveOldestPendingAge);
        _meter.CreateObservableGauge("integrios_delivery_ready_depth", ObserveReadyDeliveryDepth);
        _meter.CreateObservableGauge("integrios_delivery_oldest_ready_age_seconds", ObserveOldestReadyDeliveryAge);
        _meter.CreateObservableGauge("integrios_backlog_snapshot_age_seconds", ObserveSnapshotAge);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SampleAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.SampleInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SampleAsync(stoppingToken);
        }
    }

    private IEnumerable<Measurement<long>> ObservePendingDepth()
    {
        BacklogSnapshot? snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || Volatile.Read(ref _hasMeasurement) == 0)
        {
            return [];
        }

        return [new Measurement<long>(snapshot.PendingOutboxDepth)];
    }

    private IEnumerable<Measurement<long>> ObserveReadyDeliveryDepth() =>
        ObserveLong(snapshot => snapshot.ReadyDeliveryDepth);

    private IEnumerable<Measurement<double>> ObserveOldestPendingAge() =>
        ObserveDouble(snapshot => snapshot.OldestPendingOutboxAgeSeconds);

    private IEnumerable<Measurement<double>> ObserveOldestReadyDeliveryAge() =>
        ObserveDouble(snapshot => snapshot.OldestReadyDeliveryAgeSeconds);

    private IEnumerable<Measurement<double>> ObserveSnapshotAge()
    {
        if (Volatile.Read(ref _hasMeasurement) == 0)
        {
            return [];
        }

        return [new Measurement<double>(Stopwatch.GetElapsedTime(
            Interlocked.Read(ref _lastSuccessfulSampleTimestamp)).TotalSeconds)];
    }

    private IEnumerable<Measurement<long>> ObserveLong(Func<BacklogSnapshot, long> value) =>
        Volatile.Read(ref _snapshot) is { } snapshot && Volatile.Read(ref _hasMeasurement) != 0
            ? [new Measurement<long>(value(snapshot))]
            : [];

    private IEnumerable<Measurement<double>> ObserveDouble(Func<BacklogSnapshot, double> value) =>
        Volatile.Read(ref _snapshot) is { } snapshot && Volatile.Read(ref _hasMeasurement) != 0
            ? [new Measurement<double>(value(snapshot))]
            : [];

    internal async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            BacklogSnapshot snapshot = await _snapshotReader.ReadAsync(cancellationToken);

            Volatile.Write(ref _snapshot, snapshot);
            Interlocked.Exchange(ref _lastSuccessfulSampleTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _hasMeasurement, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not sample the backlog snapshot; retaining the last successful values.");
        }
    }
}

internal sealed record OutboxDepthMetricsOptions(TimeSpan SampleInterval);
