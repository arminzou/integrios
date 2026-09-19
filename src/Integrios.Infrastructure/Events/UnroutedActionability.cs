namespace Integrios.Infrastructure.Events;

/// Whether an unrouted Event still represents a current routing gap. Shared so that the backlog and
/// the Event inspector can never disagree about the same Event.
internal static class UnroutedActionability
{
    public static string Predicate(bool sqlServer, string eventAlias) =>
        $"({AuthorablePredicate(sqlServer, eventAlias)}) AND NOT ({CurrentMatchPredicate(sqlServer, eventAlias)})";

    public static string AuthorablePredicate(bool sqlServer, string eventAlias)
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

    public static string CurrentMatchPredicate(bool sqlServer, string eventAlias)
    {
        string matched = sqlServer
            ? $"SELECT 1 FROM OPENJSON(matching_subscription.event_types) matched WHERE LOWER(matched.value) = LOWER({eventAlias}.event_type)"
            : $"SELECT 1 FROM jsonb_array_elements_text(matching_subscription.event_types) matched(value) WHERE lower(matched.value) = lower({eventAlias}.event_type)";
        return $"""
            EXISTS (
                SELECT 1 FROM subscriptions matching_subscription
                JOIN destinations matching_destination
                  ON matching_destination.id = matching_subscription.destination_id
                 AND matching_destination.tenant_id = matching_subscription.tenant_id
                 AND matching_destination.deleted_at IS NULL
                WHERE matching_subscription.topic_id = {eventAlias}.topic_id
                  AND matching_subscription.tenant_id = {eventAlias}.tenant_id
                  AND matching_subscription.status = 'active'
                  AND matching_subscription.deleted_at IS NULL
                  AND EXISTS ({matched}))
            """;
    }
}
