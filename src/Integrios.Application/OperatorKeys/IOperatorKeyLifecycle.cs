using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.OperatorKeys;

public interface IOperatorKeyLifecycle
{
    Task<bool> HasLiveKeyAsync(CancellationToken cancellationToken);

    Task<OperatorKey> InsertAsync(
        OperatorKey operatorKey,
        CancellationToken cancellationToken);

    Task<OperatorKey> RotateAsync(
        OperatorKey newKey,
        CancellationToken cancellationToken);
}
