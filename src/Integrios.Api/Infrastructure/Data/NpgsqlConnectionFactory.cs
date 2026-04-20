using Npgsql;

namespace Integrios.Api.Infrastructure.Data;

public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return dataSource.OpenConnectionAsync(cancellationToken);
    }
}
