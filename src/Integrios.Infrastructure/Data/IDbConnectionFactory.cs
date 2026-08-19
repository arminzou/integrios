using System.Data.Common;

namespace Integrios.Infrastructure.Data;

internal interface IDbConnectionFactory
{
    DatabaseProvider Provider { get; }

    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
