using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Integrios.Infrastructure.Data;

internal sealed class SqlServerConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
