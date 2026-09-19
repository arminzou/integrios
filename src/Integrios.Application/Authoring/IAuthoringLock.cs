namespace Integrios.Application.Authoring;

public enum AuthoringResource
{
    Destination,
    Topic,
}

public interface IAuthoringLock
{
    Task<IAsyncDisposable> AcquireAsync(
        AuthoringResource resource,
        IEnumerable<Guid> resourceIds,
        CancellationToken cancellationToken);
}

public sealed class AuthoringLockConflictException()
    : Exception("Authoring is busy. Retry the request.");
