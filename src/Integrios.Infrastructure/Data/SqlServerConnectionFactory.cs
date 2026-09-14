using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Data;

internal sealed class SqlServerConnectionFactory(
    string connectionString,
    IDbContextFactory<IntegriosDbContext> contextFactory) : IDbConnectionFactory
{
    public DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        TransientConnectionRetry.OpenAsync(
            contextFactory,
            async token =>
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(token);
                return (DbConnection)connection;
            },
            cancellationToken);
}
