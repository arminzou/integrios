using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Integrios.FunctionalTests.Admin;
using Integrios.Infrastructure.Events;
using Xunit.Abstractions;

namespace Integrios.FunctionalTests.Measurement;

// Manual-only measurement harness. It is excluded from the normal test project; compile it with
// -p:EnableMonitoringMeasurement=true and set INTEGRIOS_MEASURE=1 explicitly.
// Example (PowerShell):
//   $env:INTEGRIOS_MEASURE='1'; dotnet test tests/Integrios.FunctionalTests/Integrios.FunctionalTests.csproj -p:EnableMonitoringMeasurement=true --filter FullyQualifiedName~MonitoringPlanMeasurement --logger 'console;verbosity=detailed'
// Seeds a representative volume and prints query plans/timings; it is not correctness coverage.
public sealed class MonitoringPlanMeasurement(AdminApiFixture fixture, ITestOutputHelper output)
    : IClassFixture<AdminApiFixture>
{
    private Guid MainTopic;

    private bool Pg => Environment.GetEnvironmentVariable("INTEGRIOS_TEST_DATABASE_PROVIDER") is null or "postgres";

    [Fact]
    public async Task Measure()
    {
        if (Environment.GetEnvironmentVariable("INTEGRIOS_MEASURE") != "1")
            throw new InvalidOperationException(
                "MonitoringPlanMeasurement is manual-only. Set INTEGRIOS_MEASURE=1 when running it.");
        await fixture.ResetAsync();
        var (seededEventId, deliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        var ids = await connection.QuerySingleAsync<(Guid TopicId, Guid SourceId, Guid SubscriptionId, Guid DestinationId)>(
            "SELECT e.topic_id AS TopicId, e.source_id AS SourceId, d.subscription_id AS SubscriptionId, d.destination_id AS DestinationId FROM event_deliveries d JOIN events e ON e.id = d.event_id WHERE d.id = @Id",
            new { Id = deliveryId });

        MainTopic = ids.TopicId;
        var sw = Stopwatch.StartNew();
        await SeedAsync(connection, seededEventId, ids.TopicId, ids.SourceId, ids.SubscriptionId, ids.DestinationId);
        output.WriteLine($"seed: {sw.Elapsed}");
        output.WriteLine("counts: " + string.Join(", ", await connection.QueryAsync<string>(
            "SELECT CONCAT(status, '=', COUNT(*)) FROM events GROUP BY status")));
        output.WriteLine("deliveries: " + string.Join(", ", await connection.QueryAsync<string>(
            "SELECT CONCAT(status, '=', COUNT(*)) FROM event_deliveries GROUP BY status")));

        if (Environment.GetEnvironmentVariable("INTEGRIOS_MEASURE_INDEXES") == "1")
        {
            await connection.ExecuteAsync(new CommandDefinition(Environment.GetEnvironmentVariable("INTEGRIOS_MEASURE_DDL") ?? """
                CREATE INDEX idx_events_tenant_backlog ON events (tenant_id, status, accepted_at) WHERE status IN ('accepted', 'unrouted');

                """, commandTimeout: 600));
            await connection.ExecuteAsync(new CommandDefinition(Pg ? "ANALYZE events, event_deliveries" : "EXEC sp_updatestats", commandTimeout: 600));
            output.WriteLine("with candidate indexes");
        }

        foreach (var (name, sql, parameters) in Queries())
            await MeasureAsync(connection, name, sql, parameters);
    }

    private IEnumerable<(string Name, string Sql, object Parameters)> Queries()
    {
        yield return ("backlog", TenantEventMonitoring.BacklogSql(!Pg), new { fixture.TenantId });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var end = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        yield return ("activity-7d", TenantEventMonitoring.ActivitySql(!Pg), new { fixture.TenantId, WindowStart = end.AddDays(-7), WindowEnd = end, BucketSeconds = 21600 });
        yield return ("activity-1h", TenantEventMonitoring.ActivitySql(!Pg), new { fixture.TenantId, WindowStart = end.AddHours(-1), WindowEnd = end, BucketSeconds = 300 });
        string top = Pg ? "" : "TOP (21)";
        string lim = Pg ? "LIMIT 21" : "";
        string typeLower = Pg ? "lower(e.event_type) = lower(@EventType)" : "e.event_type = @EventType";
        yield return ("playground-rare-lower", $"SELECT {top} e.id FROM events e WHERE e.tenant_id = @TenantId AND e.topic_id = @TopicId AND {typeLower} ORDER BY e.accepted_at DESC, e.id DESC {lim}", new { fixture.TenantId, TopicId = MainTopic, EventType = "Retired.Type" });
        yield return ("playground-rare-equal", $"SELECT {top} e.id FROM events e WHERE e.tenant_id = @TenantId AND e.topic_id = @TopicId AND e.event_type = @EventType ORDER BY e.accepted_at DESC, e.id DESC {lim}", new { fixture.TenantId, TopicId = MainTopic, EventType = "retired.type" });
        yield return ("ledger-common-lower", $"SELECT {top} e.id FROM events e WHERE e.tenant_id = @TenantId AND {typeLower} ORDER BY e.accepted_at DESC, e.id DESC {lim}", new { fixture.TenantId, EventType = "Recovery.Test" });
        string top101 = Pg ? "" : "TOP (101)";
        string lim101 = Pg ? "LIMIT 101" : "";
        yield return ("freshness-all", $"SELECT CAST(COUNT(*) AS INT) FROM (SELECT {top101} 1 AS n FROM events e WHERE e.tenant_id = @TenantId AND (e.accepted_at > @At OR (e.accepted_at = @At AND e.id > @Id)) {lim101}) x", new { fixture.TenantId, At = end.AddHours(-2), Id = Guid.Empty });
        yield return ("freshness-unrouted-type", $"SELECT CAST(COUNT(*) AS INT) FROM (SELECT {top101} 1 AS n FROM events e WHERE e.tenant_id = @TenantId AND e.status = 'unrouted' AND {typeLower} AND (e.accepted_at > @At OR (e.accepted_at = @At AND e.id > @Id)) {lim101}) x", new { fixture.TenantId, EventType = "recovery.test", At = end.AddMinutes(-1), Id = Guid.Empty });
        if (Environment.GetEnvironmentVariable("INTEGRIOS_MEASURE_ALL") != "1") yield break;
        yield return ("dead-join", "SELECT COUNT(*), MIN(d.failed_at) FROM event_deliveries d JOIN events e ON e.id = d.event_id WHERE e.tenant_id = @TenantId AND d.status = 'dead_lettered'", new { fixture.TenantId });
        yield return ("dead-exists", "SELECT COUNT(*), MIN(d.failed_at) FROM event_deliveries d WHERE d.status = 'dead_lettered' AND EXISTS (SELECT 1 FROM events e WHERE e.id = d.event_id AND e.tenant_id = @TenantId)", new { fixture.TenantId });
    }

    private async Task MeasureAsync(DbConnection connection, string name, string sql, object parameters)
    {
        for (int i = 0; i < 2; i++)
            await connection.QueryAsync(new CommandDefinition(sql, parameters, commandTimeout: 300));
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await connection.QueryAsync(new CommandDefinition(sql, parameters, commandTimeout: 300));
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        output.WriteLine($"=== {name}: median {times[2]:F1} ms (min {times[0]:F1}, max {times[4]:F1})");

        if (Pg)
        {
            var plan = await connection.QueryAsync<string>(new CommandDefinition(
                "EXPLAIN (ANALYZE, BUFFERS) " + sql.TrimEnd().TrimEnd(';'), parameters, commandTimeout: 300));
            foreach (string line in plan)
                output.WriteLine(line);
        }
        else
        {
            using var reader = await connection.QueryMultipleAsync(new CommandDefinition(
                "SET STATISTICS XML ON;\n" + sql + "\nSET STATISTICS XML OFF;", parameters, commandTimeout: 300));
            await reader.ReadAsync();
            string xml = (await reader.ReadAsync<string>()).First();
            var ops = new StringBuilder();
            foreach (Match op in Regex.Matches(xml, "<RelOp [^>]*PhysicalOp=\"([^\"]+)\"[^>]*>(.*?)(?=<RelOp |</RelOp>)", RegexOptions.Singleline))
            {
                string index = Regex.Match(op.Groups[2].Value, "Index=\"\\[([^\\]]+)\\]\"").Groups[1].Value;
                string actual = Regex.Match(op.Groups[2].Value, "ActualRows=\"(\\d+)\"").Groups[1].Value;
                string reads = Regex.Match(op.Groups[2].Value, "ActualLogicalReads=\"(\\d+)\"").Groups[1].Value;
                ops.AppendLine($"  {op.Groups[1].Value} {index} rows={actual} reads={reads}");
            }
            output.WriteLine(ops.ToString());
        }
    }

    private async Task SeedAsync(DbConnection c, Guid seededEventId, Guid topicId, Guid sourceId, Guid subscriptionId, Guid destinationId)
    {
        Guid otherTopic = Guid.NewGuid(), otherSource = Guid.NewGuid();
        await c.ExecuteAsync($$"""
            INSERT INTO topics (id, tenant_id, {{fixture.KeyColumn}}, name) VALUES (@OtherTopic, @OtherTenantId, 'other', 'other');
            INSERT INTO sources (id, tenant_id, connector_id, topic_id, name, type, event_types, configuration, revision, status)
            VALUES (@OtherSource, @OtherTenantId, @ConnectorId, @OtherTopic, 'other', 'event_api', '["recovery.test"]', '{}', 'r', 'active');
            """, new { OtherTopic = otherTopic, OtherSource = otherSource, fixture.OtherTenantId, ConnectorId = fixture.HttpConnectorId });

        // Main Tenant: 400k Events over ~23 days. 2k awaiting routing spread across the history,
        // 3k old unrouted (a tenth of them of a retired type), the rest routed.
        string numbers(int n) => Pg
            ? $"SELECT g FROM generate_series(1, {n}) g"
            : $"SELECT TOP ({n}) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS INT) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c";
        string uuid = Pg ? "gen_random_uuid()" : "NEWID()";
        string ago(string seconds) => Pg ? $"now() - ({seconds}) * interval '1 second'" : $"DATEADD(second, -({seconds}), SYSDATETIMEOFFSET())";
        string json = Pg ? "'{}'::jsonb" : "'{}'";
        await c.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO events (id, tenant_id, topic_id, source_id, event_type, payload, status, accepted_at)
            SELECT {uuid}, @TenantId, @TopicId, @SourceId,
                CASE WHEN n.g BETWEEN 300000 AND 303000 AND n.g % 10 = 0 THEN 'retired.type' ELSE 'recovery.test' END,
                {json},
                CASE WHEN n.g % 200 = 1 THEN 'accepted' WHEN n.g BETWEEN 300000 AND 303000 THEN 'unrouted' ELSE 'routed' END,
                {ago("n.g * 5")}
            FROM ({numbers(400000)}) n;
            INSERT INTO events (id, tenant_id, topic_id, source_id, event_type, payload, status, accepted_at)
            SELECT {uuid}, @OtherTenantId, @OtherTopic, @OtherSource, 'recovery.test', {json},
                CASE WHEN n.g % 50 = 0 THEN 'unrouted' ELSE 'routed' END, {ago("n.g * 20")}
            FROM ({numbers(100000)}) n;
            """, new { fixture.TenantId, TopicId = topicId, SourceId = sourceId, fixture.OtherTenantId, OtherTopic = otherTopic, OtherSource = otherSource }, commandTimeout: 600));

        // 250k Deliveries for the main Tenant's routed Events, one in ten dead-lettered (25k).
        string top = Pg ? "" : "TOP (250000)";
        string limit = Pg ? "LIMIT 250000" : "";
        string failed = Pg ? "e.accepted_at + interval '1 minute'" : "DATEADD(minute, 1, e.accepted_at)";
        await c.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO event_deliveries
                (id, event_id, subscription_id, destination_id, http_execution_snapshot, connector_key,
                 status, lifetime_attempt_count, retry_cycle_attempt_count, failed_at)
            SELECT {uuid}, e.id, @SubscriptionId, @DestinationId, s.http_execution_snapshot, 'http',
                CASE WHEN e.rn % 10 = 0 THEN 'dead_lettered' ELSE 'succeeded' END, 1, 1,
                CASE WHEN e.rn % 10 = 0 THEN {failed} END
            FROM (SELECT {top} id, accepted_at, ROW_NUMBER() OVER (ORDER BY accepted_at) AS rn
                  FROM events WHERE tenant_id = @TenantId AND status = 'routed' AND event_type = 'recovery.test'
                    AND id <> @SeededEventId {limit}) e
            CROSS JOIN (SELECT http_execution_snapshot FROM event_deliveries WHERE event_id = @SeededEventId) s;
            """, new { fixture.TenantId, SubscriptionId = subscriptionId, DestinationId = destinationId, SeededEventId = seededEventId }, commandTimeout: 600));

        await c.ExecuteAsync(new CommandDefinition(Pg ? "ANALYZE events, event_deliveries, sources, topics, subscriptions, destinations" : "EXEC sp_updatestats", commandTimeout: 600));
    }
}
