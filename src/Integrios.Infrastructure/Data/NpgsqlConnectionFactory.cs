using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Integrios.Infrastructure.Data;

internal sealed class NpgsqlConnectionFactory(
    NpgsqlDataSource dataSource,
    IDbContextFactory<IntegriosDbContext> contextFactory) : IDbConnectionFactory
{
    public DatabaseProvider Provider => DatabaseProvider.Postgres;

    public ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        TransientConnectionRetry.OpenAsync(
            contextFactory,
            async token => await dataSource.OpenConnectionAsync(token),
            cancellationToken);
}
