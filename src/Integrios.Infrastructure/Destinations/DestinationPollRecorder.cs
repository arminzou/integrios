using Dapper;
using Integrios.Application.Subscriber;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.Destinations;

internal sealed class DestinationPollRecorder(IDbConnectionFactory connectionFactory) : IDestinationPollRecorder
{
    public async Task RecordPollAsync(Guid destinationId, DateTimeOffset polledAt, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE destinations SET last_polled_at = @PolledAt
            WHERE id = @Id AND (last_polled_at IS NULL OR last_polled_at <= @Stale)
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql,
            new { Id = destinationId, PolledAt = polledAt, Stale = polledAt - DestinationPoll.Resolution },
            cancellationToken: cancellationToken));
    }
}
