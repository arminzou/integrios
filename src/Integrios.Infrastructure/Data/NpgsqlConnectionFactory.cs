using System.Data.Common;
using Npgsql;

namespace Integrios.Infrastructure.Data;

internal sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public DatabaseProvider Provider => DatabaseProvider.Postgres;

    public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        TransientConnectionRetry.OpenAsync(
            async token => await dataSource.OpenConnectionAsync(token),
            cancellationToken);
}
