using Dapper;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Ingestion;
using Integrios.Application.Telemetry;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Events;

internal sealed class TenantEventHistory(IDbConnectionFactory connectionFactory, IDataProtectionProvider dataProtectionProvider)
    : ITenantEventHistory
{
    public async Task<(IReadOnlyList<EventListItemDto> Items, string? NextCursor)> ListAsync(
        Guid tenantId,
        TenantEventFilter filter,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        string cursorScope = string.Join(':',
            "events", tenantId.ToString("N"),
            filter.Status is { } s ? EventStatusMap.ToDbValue(s) : "all",
            filter.DeliveryStatus ?? "all",
            filter.SourceId?.ToString("N") ?? "all",
            filter.TopicId?.ToString("N") ?? "all",
            filter.SourceEventId ?? "all",
            filter.AcceptedFrom?.UtcTicks.ToString() ?? "all",
            filter.AcceptedTo?.UtcTicks.ToString() ?? "all");
        DateTimeOffset cursorAcceptedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorAcceptedAt, out cursorId))
            throw new InvalidCursorException();

        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        var where = new List<string> { "e.tenant_id = @TenantId" };
        if (filter.Status is not null) where.Add("e.status = @Status");
        if (filter.SourceId is not null) where.Add("e.source_id = @SourceId");
        if (filter.TopicId is not null) where.Add("e.topic_id = @TopicId");
        if (filter.SourceEventId is not null)
            where.Add(sqlServer
                ? "e.source_event_id = @SourceEventId COLLATE Latin1_General_BIN2"
                : "e.source_event_id = @SourceEventId");
        if (filter.AcceptedFrom is not null) where.Add("e.accepted_at >= @AcceptedFrom");
        if (filter.AcceptedTo is not null) where.Add("e.accepted_at <= @AcceptedTo");
        if (filter.DeliveryStatus is not null)
            where.Add("EXISTS (SELECT 1 FROM event_deliveries d WHERE d.event_id = e.id AND d.status = @DeliveryStatus)");
        if (hasCursor)
            where.Add("(e.accepted_at < @CursorAcceptedAt OR (e.accepted_at = @CursorAcceptedAt AND e.id < @CursorId))");

        string sql = $"""
            SELECT {(sqlServer ? "TOP (@Take) " : string.Empty)}
                e.id              AS EventId,
                e.source_id       AS SourceId,
                e.topic_id        AS TopicId,
                e.source_event_id AS SourceEventId,
                e.event_type      AS EventType,
                e.status          AS Status,
                e.accepted_at     AS AcceptedAt,
                (SELECT MAX(traceparent) FROM outbox WHERE event_id = e.id) AS Traceparent,
                (SELECT COUNT(*) FROM event_deliveries d WHERE d.event_id = e.id AND d.status = 'pending')       AS Pending,
                (SELECT COUNT(*) FROM event_deliveries d WHERE d.event_id = e.id AND d.status = 'in_flight')     AS InFlight,
                (SELECT COUNT(*) FROM event_deliveries d WHERE d.event_id = e.id AND d.status = 'succeeded')     AS Succeeded,
                (SELECT COUNT(*) FROM event_deliveries d WHERE d.event_id = e.id AND d.status = 'dead_lettered') AS DeadLettered
            FROM events e
            WHERE {string.Join(" AND ", where)}
            ORDER BY e.accepted_at DESC, e.id DESC
            {(sqlServer ? string.Empty : "LIMIT @Take")};
            """;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        List<EventListRow> rows = (await connection.QueryAsync<EventListRow>(new CommandDefinition(sql, new
        {
            TenantId = tenantId,
            Status = filter.Status is { } status ? EventStatusMap.ToDbValue(status) : null,
            filter.DeliveryStatus,
            filter.SourceId,
            filter.TopicId,
            filter.SourceEventId,
            filter.AcceptedFrom,
            filter.AcceptedTo,
            CursorAcceptedAt = cursorAcceptedAt,
            CursorId = cursorId,
            Take = limit + 1,
        }, cancellationToken: cancellationToken))).ToList();

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = PageCursor.Encode(dataProtectionProvider, cursorScope, rows[^1].AcceptedAt, rows[^1].EventId, DateTimeOffset.UtcNow);
        }

        return (rows.Select(row => new EventListItemDto
        {
            EventId = row.EventId,
            SourceId = row.SourceId,
            TopicId = row.TopicId,
            SourceEventId = row.SourceEventId,
            EventType = row.EventType,
            Status = EventStatusMap.FromDbValue(row.Status),
            AcceptedAt = row.AcceptedAt,
            TraceId = ActivitySources.TryParseTraceparent(row.Traceparent, out var context) ? context.TraceId.ToString() : null,
            Deliveries = new EventDeliveryCounts(row.Pending, row.InFlight, row.Succeeded, row.DeadLettered),
        }).ToList(), nextCursor);
    }

    private sealed record EventListRow
    {
        public Guid EventId { get; init; }
        public Guid? SourceId { get; init; }
        public Guid? TopicId { get; init; }
        public string? SourceEventId { get; init; }
        public string EventType { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTimeOffset AcceptedAt { get; init; }
        public string? Traceparent { get; init; }
        public int Pending { get; init; }
        public int InFlight { get; init; }
        public int Succeeded { get; init; }
        public int DeadLettered { get; init; }
    }
}
