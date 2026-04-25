using System.Data.Common;
using Integrios.Domain.Abstractions.Data;
using Npgsql;

namespace Integrios.Infrastructure.Data;

public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await dataSource.OpenConnectionAsync(cancellationToken);
    }
}
