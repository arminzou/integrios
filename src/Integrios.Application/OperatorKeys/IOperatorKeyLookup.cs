using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.OperatorKeys;

public interface IOperatorKeyLookup
{
    Task<OperatorKey?> FindActiveByPublicKeyAsync(
        string publicKey,
        CancellationToken cancellationToken);
}
