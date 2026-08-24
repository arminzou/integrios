namespace Integrios.Application.Authoring.Connections;

public interface IConnectionAuthoringLock
{
    Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<Guid> connectionIds,
        CancellationToken cancellationToken);
}
