using Dapper;
using Integrios.Application.DeliveryApi;
using Integrios.Infrastructure.Data;

namespace Integrios.Infrastructure.SubscriberKeys;

internal sealed class SubscriberKeyUseRecorder(IDbConnectionFactory connectionFactory) : ISubscriberKeyUseRecorder
{
    public async Task RecordUseAsync(Guid subscriberKeyId, DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE subscriber_keys SET last_used_at = @UsedAt
            WHERE id = @Id AND revoked_at IS NULL AND deleted_at IS NULL
              AND (last_used_at IS NULL OR last_used_at <= @Stale)
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql,
            new { Id = subscriberKeyId, UsedAt = usedAt, Stale = usedAt - SubscriberKeyUse.Resolution },
            cancellationToken: cancellationToken));
    }
}
