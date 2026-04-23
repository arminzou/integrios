using Npgsql;

namespace Integrios.Worker.Infrastructure.Data;

public interface IDbConnectionFactory
{
    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
