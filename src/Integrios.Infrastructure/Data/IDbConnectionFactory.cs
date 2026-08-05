using System.Data.Common;

namespace Integrios.Infrastructure.Data;

internal interface IDbConnectionFactory
{
    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
