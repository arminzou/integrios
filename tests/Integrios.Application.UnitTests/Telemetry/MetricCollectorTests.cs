using System.Diagnostics.Metrics;
using Integrios.Application.Telemetry;
using Integrios.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.Application.UnitTests;

public sealed class MetricCollectorTests
{
    private static readonly HashSet<string> AllowedMetricLabels = ["connector_key", "http_status_class", "result", "transport"];

    [Fact]
    public void ForInstrument_ReturnsStableSnapshot_WhenAnotherMeasurementArrives()
    {
        var meterName = $"integrios.tests.{Guid.NewGuid():N}";
        using var collector = new MetricCollector(meterName);
        using var meter = new Meter(meterName);
        var counter = meter.CreateCounter<long>("test_counter");

        counter.Add(1);
        using var measurements = collector.ForInstrument("test_counter").GetEnumerator();

        measurements.MoveNext().ShouldBeTrue();
        counter.Add(1);
        measurements.MoveNext().ShouldBeFalse();
    }

    [Fact]
    public void IntegriosMetrics_EmitOnlyAllowedLabels()
    {
        using var collector = new MetricCollector(IntegriosMetrics.MeterName);
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new IntegriosMetrics(provider.GetRequiredService<IMeterFactory>());

        metrics.RecordEventIngested();
        metrics.RecordEventUnrouted();
        metrics.RecordQueueSourceError("azure_service_bus");
        metrics.RecordFanoutRowsCreated(1);
        metrics.RecordDeliverySucceeded("http");
        metrics.RecordDeliveryFailed("http", "5xx");
        metrics.RecordDeliveryDeadLettered("http");
        metrics.RecordDeliverySecretResolutionFailure("http");
        metrics.RecordDeliveryRequestConstructionFailure("http");
        metrics.RecordDeliveryStaleFinalization();
        metrics.RecordDeliveryAttemptDuration(1, "success", "http");

        AssertAllowed(collector.AllTagKeys);
    }

    [Fact]
    public void MetricLabelAllowlist_RejectsPlantedForbiddenKey() =>
        Should.Throw<ShouldAssertException>(() => AssertAllowed(["tenant_id"]));

    private static void AssertAllowed(IEnumerable<string> keys) =>
        keys.ShouldAllBe(key => AllowedMetricLabels.Contains(key));
}
