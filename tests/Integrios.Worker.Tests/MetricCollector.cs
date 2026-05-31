using System.Diagnostics.Metrics;

namespace Integrios.Worker.Tests;

// Captures synchronous instrument measurements for a single meter via MeterListener.
// Start it before the meter's instruments are created (i.e. before the first handler runs).
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();

    public List<Measurement> Longs { get; } = [];
    public List<Measurement> Doubles { get; } = [];

    public MetricCollector(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            Longs.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Doubles.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));

        _listener.Start();
    }

    public IEnumerable<Measurement> ForInstrument(string name) =>
        Longs.Concat(Doubles).Where(m => m.Instrument == name);

    public IEnumerable<string> AllTagKeys =>
        Longs.Concat(Doubles).SelectMany(m => m.Tags.Keys).Distinct();

    private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var tag in tags)
            dictionary[tag.Key] = tag.Value;
        return dictionary;
    }

    public void Dispose() => _listener.Dispose();

    internal sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string key) => Tags.TryGetValue(key, out var value) ? value : null;
    }
}
