using System.Text.Json;
using Dapper;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.EventMonitoring;
using Integrios.Application.Telemetry;
using Integrios.Domain.Enums;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Events;

internal sealed class TenantEventHistory(IDbConnectionFactory connectionFactory, IDataProtectionProvider dataProtectionProvider)
    : ITenantEventHistory
{
    // A watermark marks where a first page ended, not where to continue it, so it is protected under
    // its own scope: a page cursor can never be replayed as a watermark, or the reverse.
    private const string WatermarkScope = "watermark:";

    public async Task<(IReadOnlyList<EventListItemDto> Items, string? NextCursor, string? Watermark)> ListAsync(
        Guid tenantId,
        TenantEventFilter filter,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        string cursorScope = Scope(tenantId, filter);
        DateTimeOffset cursorAcceptedAt = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorAcceptedAt, out cursorId))
            throw new InvalidCursorException();

        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        List<string> where = Where(filter);
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
        DynamicParameters parameters = Parameters(tenantId, filter);
        parameters.Add("CursorAcceptedAt", cursorAcceptedAt);
        parameters.Add("CursorId", cursorId);
        parameters.Add("Take", limit + 1);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        List<EventListRow> rows = (await connection.QueryAsync<EventListRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = PageCursor.Encode(dataProtectionProvider, cursorScope, rows[^1].AcceptedAt, rows[^1].EventId, DateTimeOffset.UtcNow);
        }

        // Ordered newest first, so a first page's newest Event is its first row. A page that matched
        // nothing has shown the reader nothing, so every Event that later matches is new to it,
        // including one stamped before this read that committed after it.
        string? watermark = hasCursor
            ? null
            : PageCursor.Encode(
                dataProtectionProvider,
                WatermarkScope + cursorScope,
                rows.Count > 0 ? rows[0].AcceptedAt : DateTimeOffset.UnixEpoch,
                rows.Count > 0 ? rows[0].EventId : Guid.Empty,
                DateTimeOffset.UtcNow);

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
        }).ToList(), nextCursor, watermark);
    }

    public async Task<int> CountNewerAsync(
        Guid tenantId,
        TenantEventFilter filter,
        string watermark,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!PageCursor.TryDecode(dataProtectionProvider, watermark, WatermarkScope + Scope(tenantId, filter), out DateTimeOffset acceptedAt, out Guid id))
            throw new InvalidCursorException();

        bool sqlServer = connectionFactory.Provider == DatabaseProvider.SqlServer;
        List<string> where = Where(filter);
        where.Add("(e.accepted_at > @WatermarkAcceptedAt OR (e.accepted_at = @WatermarkAcceptedAt AND e.id > @WatermarkId))");
        // Counting stops at the limit, so the cost is bounded by the limit rather than by how long the
        // screen stayed open.
        string sql = $"""
            SELECT CAST(COUNT(*) AS INT) FROM (
                SELECT {(sqlServer ? "TOP (@Take) " : string.Empty)}1 AS newer
                FROM events e
                WHERE {string.Join(" AND ", where)}
                {(sqlServer ? string.Empty : "LIMIT @Take")}
            ) newer_events;
            """;
        DynamicParameters parameters = Parameters(tenantId, filter);
        parameters.Add("WatermarkAcceptedAt", acceptedAt);
        parameters.Add("WatermarkId", id);
        parameters.Add("Take", limit);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    // SourceEventId is Operator-supplied free text, unlike the enum and Guid filters every other
    // capability scopes by: it could itself read "all" or carry a delimiter or a newline. JSON
    // (present vs. the literal null, and every string escaped) keeps the scope unambiguous
    // instead of colon-joining raw values, which could collide two different filter sets onto
    // the same scope or break PageCursor's own newline-delimited framing. Event type is lowered
    // because it matches ignoring case, so two spellings of one filter share a scope.
    private static string Scope(Guid tenantId, TenantEventFilter filter) =>
        JsonSerializer.Serialize(new CursorScopeKey(
            tenantId,
            filter.Status is { } s ? EventStatusMap.ToDbValue(s) : null,
            filter.DeliveryStatus,
            filter.SourceId,
            filter.TopicId,
            filter.SourceEventId,
            filter.AcceptedFrom?.ToUniversalTime(),
            filter.AcceptedTo?.ToUniversalTime(),
            filter.EventType?.ToLowerInvariant()));

    // The one interpretation of the Event-history filters, shared by the ledger and its freshness
    // count so the two can never disagree about which Events a filter matches.
    private static List<string> Where(TenantEventFilter filter)
    {
        var where = new List<string> { "e.tenant_id = @TenantId" };
        if (filter.Status is not null)
            where.Add("e.status = @Status");
        if (filter.SourceId is not null)
            where.Add("e.source_id = @SourceId");
        if (filter.TopicId is not null)
            where.Add("e.topic_id = @TopicId");
        if (filter.SourceEventId is not null)
            where.Add("e.source_event_id = @SourceEventId");
        // Exact, ignoring case, against the type stored on the Event, so a type no Source declares any
        // more is still found. The column's collation ignores case on both providers.
        if (filter.EventType is not null)
            where.Add("e.event_type = @EventType");
        if (filter.AcceptedFrom is not null)
            where.Add("e.accepted_at >= @AcceptedFrom");
        if (filter.AcceptedTo is not null)
            where.Add("e.accepted_at <= @AcceptedTo");
        if (filter.DeliveryStatus is not null)
            where.Add("EXISTS (SELECT 1 FROM event_deliveries d WHERE d.event_id = e.id AND d.status = @DeliveryStatus)");
        return where;
    }

    private static DynamicParameters Parameters(Guid tenantId, TenantEventFilter filter)
    {
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId);
        parameters.Add("Status", filter.Status is { } status ? EventStatusMap.ToDbValue(status) : null);
        parameters.Add("DeliveryStatus", filter.DeliveryStatus);
        parameters.Add("SourceId", filter.SourceId);
        parameters.Add("TopicId", filter.TopicId);
        parameters.Add("SourceEventId", filter.SourceEventId);
        parameters.Add("EventType", filter.EventType);
        parameters.Add("AcceptedFrom", filter.AcceptedFrom?.ToUniversalTime());
        parameters.Add("AcceptedTo", filter.AcceptedTo?.ToUniversalTime());
        return parameters;
    }

    private sealed record CursorScopeKey(
        Guid TenantId,
        string? Status,
        string? DeliveryStatus,
        Guid? SourceId,
        Guid? TopicId,
        string? SourceEventId,
        DateTimeOffset? AcceptedFrom,
        DateTimeOffset? AcceptedTo,
        string? EventType);

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
