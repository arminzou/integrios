namespace Integrios.Application.Ingestion;

// The acceptance transaction's verdict on whether a Source may publish an Event right now. It is
// given the declarations read inside that transaction, under a lock a concurrent disable or
// declaration change has to wait for, so a resolver or broker receiver holding an older snapshot
// cannot accept past a change that has committed.
public static class SourceAuthority
{
    // `declared` is null when no Enabled Source matches the Tenant, Source, and Topic.
    public static void Ensure(IReadOnlyList<string>? declared, string eventType)
    {
        if (declared is null)
            throw new EventAcceptanceException("The Source is not enabled for the requested Topic.");
        if (!declared.Contains(eventType, StringComparer.OrdinalIgnoreCase))
            throw new EventAcceptanceException($"The Source does not declare the Event type '{eventType}'.");
    }
}
