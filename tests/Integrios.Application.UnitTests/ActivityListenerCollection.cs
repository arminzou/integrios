namespace Integrios.Application.UnitTests;

// ActivityListener registration is process-wide, so two ActivityCollectors alive at once each see
// the other's spans. Test classes that build one join this collection to run serially.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActivityListenerCollection
{
    public const string Name = "Activity listeners";
}
