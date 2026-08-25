using System.Diagnostics.Metrics;
using Integrios.Tests.Shared;

namespace Integrios.Application.UnitTests;

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

        measurements.MoveNext().ShouldBeTrue();
        counter.Add(1);
        measurements.MoveNext().ShouldBeFalse();
    }
}
