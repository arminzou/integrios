namespace Integrios.Application.Connections;

public interface IConnectionAuthoringLock
{
    Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<Guid> connectionIds,
        CancellationToken cancellationToken);
}
