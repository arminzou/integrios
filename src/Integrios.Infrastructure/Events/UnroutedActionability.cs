namespace Integrios.Infrastructure.Events;

/// Whether an unrouted Event could still be routed by authoring a Subscription: its Topic is live
/// and at least one non-deleted Source on that Topic declares its exact Event type, ignoring case.
/// Shared so that the backlog and the Event inspector can never disagree about the same Event.
internal static class UnroutedActionability
{
    public static string Predicate(bool sqlServer, string eventAlias)
    {
        // Both providers compare lowered values: SQL Server's default collation would already ignore
        // case, but a case-sensitive database collation must not quietly change the answer.
        string declared = sqlServer
            ? $"SELECT 1 FROM OPENJSON(declaring.event_types) declared WHERE LOWER(declared.value) = LOWER({eventAlias}.event_type)"
            : $"SELECT 1 FROM jsonb_array_elements_text(declaring.event_types) declared(value) WHERE lower(declared.value) = lower({eventAlias}.event_type)";
        return $"""
            EXISTS (
                SELECT 1 FROM topics live_topic
                JOIN sources declaring ON declaring.topic_id = live_topic.id AND declaring.tenant_id = live_topic.tenant_id
                    AND declaring.deleted_at IS NULL
                WHERE live_topic.id = {eventAlias}.topic_id AND live_topic.tenant_id = {eventAlias}.tenant_id
                  AND live_topic.deleted_at IS NULL
                  AND EXISTS ({declared}))
            """;
    }
}
