using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Integrios.Infrastructure.Data;

internal sealed class SqlServerConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        TransientConnectionRetry.OpenAsync(
            async token =>
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(token);
                return connection;
            },
            cancellationToken);
}
