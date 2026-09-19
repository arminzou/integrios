namespace Integrios.Application.Authoring.Topics;

/// One Source's Event-type declaration, read for the Topic it publishes to.
public sealed record SourceDeclaration(Guid SourceId, Guid TopicId, IReadOnlyList<string> EventTypes);

/// One Subscription's Event-type selection, read for the Topic it routes from.
public sealed record SubscriptionSelection(string Name, IReadOnlyList<string> EventTypes);

/// A Topic owns no Event types. It exposes the union of what its Sources declare, and Subscriptions on
/// it select from that union, so these rules are the only place the two meet.
public static class TopicEventTypes
{
    // Matching ignores case, and Sources on one Topic must agree on spelling, so a case-insensitive
    // union loses nothing. Sorted so a Topic reads the same however its Sources were authored.
    public static IReadOnlyList<string> Union(IEnumerable<SourceDeclaration> declarations) =>
        declarations
            .SelectMany(declaration => declaration.EventTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Several Sources declaring one type assert it means the same Event from each, so they spell it
    // the same way. Normalizing a second spelling would change what one Source's Operator wrote.
    public static void EnsureSameSpelling(IReadOnlyList<string> declared, IEnumerable<SourceDeclaration> otherSources)
    {
        IReadOnlyList<string> exposed = Union(otherSources);
        foreach (string eventType in declared)
        {
            string? existing = exposed.FirstOrDefault(
                candidate => string.Equals(candidate, eventType, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing != eventType)
            {
                throw new AuthoringConflictException(
                    $"The Topic already exposes the Event type '{existing}'. Declare '{eventType}' with that exact spelling.");
            }
        }
    }

    // A type stays available while any other Source still declares it. Withdrawing the last
    // declaration a Subscription selects would leave that Subscription waiting for an Event intake
    // can no longer accept, so the Subscription changes first; nothing here edits it.
    public static void EnsureNoSelectionLosesItsDeclaration(
        IEnumerable<string> withdrawn,
        IEnumerable<SourceDeclaration> remainingSources,
        IEnumerable<SubscriptionSelection> selections)
    {
        IReadOnlyList<string> remaining = Union(remainingSources);
        var orphaned = withdrawn
            .Where(eventType => !remaining.Contains(eventType, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SubscriptionSelection selection in selections)
        {
            string? selected = selection.EventTypes.FirstOrDefault(orphaned.Contains);
            if (selected is not null)
            {
                throw new AuthoringConflictException(
                    $"The Subscription '{selection.Name}' selects the Event type '{selected}', and no other Source on "
                    + "this Topic declares it. Change or delete that Subscription first.");
            }
        }
    }
}
