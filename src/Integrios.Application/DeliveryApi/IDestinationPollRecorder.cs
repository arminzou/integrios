namespace Integrios.Application.DeliveryApi;

public interface IDestinationPollRecorder
{
    Task RecordPollAsync(Guid destinationId, DateTimeOffset polledAt, CancellationToken cancellationToken);
}

public static class DestinationPoll
{
    public static readonly TimeSpan Resolution = TimeSpan.FromMinutes(1);
}
