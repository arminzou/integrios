using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Authoring.Topics;

/// <summary>
/// What a Topics list is scoped to, and what the cursor is scoped by with it. A filter that does not
/// reach the cursor scope would let a stale cursor page a different set under a token the caller
/// cannot tell apart.
/// </summary>
public sealed record TopicListFilter(OperationalStatus? Status = null, string? NameContains = null);

/// <summary>
/// A Topic with how many Subscriptions match it. The count answers the question the list is read to
/// answer — a Topic nothing subscribes to accepts Events and routes none of them — and it is counted
/// where the row is read rather than left for the caller to fetch per row.
/// </summary>
public sealed record TopicListRow(Topic Topic, int SubscriptionCount);
