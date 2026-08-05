using System.Data.Common;
using Npgsql;

namespace Integrios.Infrastructure.Data;

internal sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await dataSource.OpenConnectionAsync(cancellationToken);
    }
}
