using System.Diagnostics.Metrics;

namespace Integrios.Tests.Shared;

// Captures synchronous instrument measurements for a single meter via MeterListener.
// Start it before the meter's instruments are created (i.e. before the first handler runs).
public sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _measurementsLock = new();
    private readonly List<Measurement> _longs = [];
    private readonly List<Measurement> _doubles = [];

    public MetricCollector(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_measurementsLock)
                _longs.Add(new Measurement(instrument.Name, value, ToDictionary(tags)));
        });

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (_measurementsLock)
                _doubles.Add(new Measurement(instrument.Name, value, ToDictionary(tags)));
        });

        _listener.Start();
    }

    public IReadOnlyList<Measurement> ForInstrument(string name)
    {
        lock (_measurementsLock)
            return _longs.Concat(_doubles).Where(m => m.Instrument == name).ToArray();
    }

    public void CollectObservableInstruments() => _listener.RecordObservableInstruments();

    public IReadOnlyList<string> AllTagKeys
    {
        get
        {
            lock (_measurementsLock)
                return _longs.Concat(_doubles).SelectMany(m => m.Tags.Keys).Distinct().ToArray();
        }
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var tag in tags)
            dictionary[tag.Key] = tag.Value;
        return dictionary;
    }

    public void Dispose() => _listener.Dispose();

    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string key) => Tags.TryGetValue(key, out var value) ? value : null;
    }
}
