using System.Diagnostics.Metrics;

namespace Integrios.Worker.Tests;

public sealed class MetricCollectorTests
{
    [Fact]
    public void ForInstrument_ReturnsStableSnapshot_WhenAnotherMeasurementArrives()
    {
        var meterName = $"integrios.tests.{Guid.NewGuid():N}";
        using var collector = new MetricCollector(meterName);
        using var meter = new Meter(meterName);
        var counter = meter.CreateCounter<long>("test_counter");

        counter.Add(1);
        using var measurements = collector.ForInstrument("test_counter").GetEnumerator();

        Assert.True(measurements.MoveNext());
        counter.Add(1);
        Assert.False(measurements.MoveNext());
    }
}
