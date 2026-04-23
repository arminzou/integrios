using Npgsql;

namespace Integrios.Worker.Infrastructure.Data;

public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => dataSource.OpenConnectionAsync(cancellationToken);
}
