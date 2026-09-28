namespace Integrios.Application.UnitTests;

// ActivityListener and MeterListener registration is process-wide, and meters are matched by name,
// so a collector alive during another test sees that test's spans and measurements. Test classes
// that build an ActivityCollector or MetricCollector join this collection to run serially.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryListenerCollection
{
    public const string Name = "Telemetry listeners";
}
