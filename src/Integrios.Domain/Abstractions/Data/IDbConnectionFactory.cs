using System.Data.Common;

namespace Integrios.Domain.Abstractions.Data;

public interface IDbConnectionFactory
{
    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
