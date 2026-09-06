namespace Integrios.Application.Authoring.TenantApiKeys;

/// <summary>
/// Records that a TenantApiKey authenticated a request, so an Operator deciding whether a key is
/// safe to revoke has evidence beside the control that revokes it.
/// </summary>
/// <remarks>
/// Separate from <see cref="IActiveTenantApiKeyLookup"/> on purpose. Authenticating a request is a
/// read; recording that it happened is a write, and folding the second into the first would put an
/// UPDATE behind a name that promises a lookup.
/// </remarks>
public interface ITenantApiKeyUseRecorder
{
    Task RecordUseAsync(Guid tenantApiKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken);
}

/// <summary>
/// How precisely last use is tracked. Deliberately coarse: this answers "is anything still sending
/// with this key", which is a question about days, and writing on every authenticated request would
/// put an UPDATE on the busiest path in the platform — contending hardest on exactly the keys that
/// carry the most traffic, since the hottest key is the hottest row.
/// </summary>
public static class TenantApiKeyUse
{
    /// <summary>
    /// One write per key per hour rather than one per request. A key sending continuously is
    /// recorded within the hour; a key that has genuinely stopped shows its true last hour.
    /// </summary>
    public static readonly TimeSpan Resolution = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether this use is worth a write, given what the authenticating read already loaded. The
    /// check is free — the row is in hand — so the common request costs no extra round trip.
    /// </summary>
    public static bool ShouldRecord(DateTimeOffset? lastUsedAt, DateTimeOffset now) =>
        lastUsedAt is null || now - lastUsedAt.Value >= Resolution;
}
