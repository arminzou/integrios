namespace Integrios.Application.DeliveryApi;

public interface ISubscriberKeyUseRecorder
{
    Task RecordUseAsync(Guid subscriberKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken);
}

public static class SubscriberKeyUse
{
    public static readonly TimeSpan Resolution = TimeSpan.FromHours(1);

    public static bool ShouldRecord(DateTimeOffset? lastUsedAt, DateTimeOffset now) =>
        lastUsedAt is null || now - lastUsedAt.Value >= Resolution;
}
