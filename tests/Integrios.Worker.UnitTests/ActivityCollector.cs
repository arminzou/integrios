using System.Diagnostics;

namespace Integrios.Worker.UnitTests;

// Captures activities produced by a single ActivitySource. Create it before the spans are
// started; the AllData sample makes the source actually emit activities under test.
internal sealed class ActivityCollector : IDisposable
{
    private readonly ActivityListener _listener;

    public List<Activity> Activities { get; } = [];

    public ActivityCollector(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => Activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity Single(string operationName) =>
        Activities.Single(a => a.OperationName == operationName);

    public void Dispose() => _listener.Dispose();
}
