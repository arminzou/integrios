namespace Integrios.Application.Authoring.Destinations;

public interface IDestinationAuthoringLock
{
    Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<Guid> destinationIds,
        CancellationToken cancellationToken);
}
