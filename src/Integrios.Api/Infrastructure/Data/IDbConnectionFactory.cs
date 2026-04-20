using Npgsql;

namespace Integrios.Api.Infrastructure.Data;

public interface IDbConnectionFactory
{
    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
