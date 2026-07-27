using System.Diagnostics.Metrics;

namespace Integrios.Application.Telemetry;

// Instrument names omit the `_total` suffix: the Prometheus exporter appends it to
// counters, yielding the documented `integrios_*_total` exposition names. Labels are
// platform-owned only (integration_key, http_status_class, result) — never tenant-controlled.
public sealed class IntegriosMetrics
{
    public const string MeterName = "integrios.application";

    private readonly Counter<long> _eventsIngested;
    private readonly Counter<long> _eventsUnrouted;
    private readonly Counter<long> _fanoutRowsCreated;
    private readonly Counter<long> _deliveriesSucceeded;
    private readonly Counter<long> _deliveriesFailed;
    private readonly Counter<long> _deliveriesDeadLettered;
    private readonly Counter<long> _deliverySecretResolutionFailures;
    private readonly Counter<long> _deliveryRequestConstructionFailures;
    private readonly Counter<long> _deliveryStaleFinalizations;
    private readonly Histogram<double> _deliveryAttemptDuration;

    public IntegriosMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _eventsIngested = meter.CreateCounter<long>("integrios_events_ingested");
        _eventsUnrouted = meter.CreateCounter<long>("integrios_events_unrouted");
        _fanoutRowsCreated = meter.CreateCounter<long>("integrios_fanout_rows_created");
        _deliveriesSucceeded = meter.CreateCounter<long>("integrios_deliveries_succeeded");
        _deliveriesFailed = meter.CreateCounter<long>("integrios_deliveries_failed");
        _deliveriesDeadLettered = meter.CreateCounter<long>("integrios_deliveries_dead_lettered");
        _deliverySecretResolutionFailures = meter.CreateCounter<long>("integrios_delivery_secret_resolution_failures");
        _deliveryRequestConstructionFailures = meter.CreateCounter<long>("integrios_delivery_request_construction_failures");
        _deliveryStaleFinalizations = meter.CreateCounter<long>("integrios_delivery_stale_finalizations");
        _deliveryAttemptDuration = meter.CreateHistogram<double>("integrios_delivery_attempt_duration_seconds");
    }

    public void RecordEventIngested() => _eventsIngested.Add(1);

    public void RecordEventUnrouted() => _eventsUnrouted.Add(1);

    public void RecordFanoutRowsCreated(int count) => _fanoutRowsCreated.Add(count);

    public void RecordDeliverySucceeded(string integrationKey) =>
        _deliveriesSucceeded.Add(1, new KeyValuePair<string, object?>("integration_key", integrationKey));

    public void RecordDeliveryFailed(string integrationKey, string httpStatusClass) =>
        _deliveriesFailed.Add(
            1,
            new KeyValuePair<string, object?>("integration_key", integrationKey),
            new KeyValuePair<string, object?>("http_status_class", httpStatusClass));

    public void RecordDeliveryDeadLettered(string integrationKey) =>
        _deliveriesDeadLettered.Add(1, new KeyValuePair<string, object?>("integration_key", integrationKey));

    public void RecordDeliverySecretResolutionFailure(string integrationKey) =>
        _deliverySecretResolutionFailures.Add(1, new KeyValuePair<string, object?>("integration_key", integrationKey));

    public void RecordDeliveryRequestConstructionFailure(string integrationKey) =>
        _deliveryRequestConstructionFailures.Add(1, new KeyValuePair<string, object?>("integration_key", integrationKey));

    public void RecordDeliveryStaleFinalization() => _deliveryStaleFinalizations.Add(1);

    public void RecordDeliveryAttemptDuration(double seconds, string result, string integrationKey) =>
        _deliveryAttemptDuration.Record(
            seconds,
            new KeyValuePair<string, object?>("result", result),
            new KeyValuePair<string, object?>("integration_key", integrationKey));
}
