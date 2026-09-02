using System.Data.Common;
using System.Net;
using System.Text.Json;
using Dapper;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class TenantEventHistoryTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task History_IsNewestFirstFiltersByItsApprovedFieldsAndKeepsEventAndDeliveryStateDistinct()
    {
        // The seeded Event is routed with one dead-lettered delivery, plus a second delivery on the
        // same Event so a delivery filter cannot duplicate it.
        var (deadLetteredEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (olderEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        Guid secondDeliveryId = Guid.NewGuid();
        await ExecuteAsync($$$"""
            UPDATE events SET accepted_at = {{{fixture.Now}}} WHERE id = @NewerId;
            INSERT INTO event_deliveries
                (id, event_id, subscription_id, destination_connection_id, http_execution_snapshot, connector_key,
                 status, lifetime_attempt_count, retry_cycle_attempt_count, failed_at)
            SELECT @SecondDeliveryId, @NewerId, subscription_id, destination_connection_id, http_execution_snapshot,
                   connector_key, 'dead_lettered', 1, 1, {{{fixture.Now}}}
            FROM event_deliveries WHERE event_id = @OtherEventId;
            """,
            new { NewerId = deadLetteredEventId, SecondDeliveryId = secondDeliveryId, OtherEventId = olderEventId });

        JsonElement all = await GetAsync($"/admin/tenants/{fixture.TenantId}/events");
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events")).ShouldBe([deadLetteredEventId, olderEventId]);

        JsonElement newest = all.GetProperty("items")[0];
        newest.GetProperty("status").GetString().ShouldBe("routed");
        newest.GetProperty("deliveries").GetProperty("dead_lettered").GetInt32().ShouldBe(2);

        // One Event with two matching deliveries appears exactly once.
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?delivery_status=dead_lettered"))
            .Count(id => id == deadLetteredEventId).ShouldBe(1);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?delivery_status=succeeded")).ShouldBeEmpty();
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?status=routed")).ShouldContain(deadLetteredEventId);
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?status=accepted")).ShouldBeEmpty();

        Guid sourceId = newest.GetProperty("source_id").GetGuid();
        Guid topicId = newest.GetProperty("topic_id").GetGuid();
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?source_id={sourceId}&topic_id={topicId}"))
            .ShouldBe([deadLetteredEventId]);

        // Each seeded Event owns its own Source and Topic, so crossing them must match nothing. Without
        // this, swapping or dropping either predicate would still pass.
        JsonElement older = all.GetProperty("items")[1];
        Guid otherTopicId = older.GetProperty("topic_id").GetGuid();
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?source_id={sourceId}&topic_id={otherTopicId}"))
            .ShouldBeEmpty();

        string acceptedAt = newest.GetProperty("accepted_at").GetString()!;
        (await ListIdsAsync($"/admin/tenants/{fixture.TenantId}/events?accepted_from={Uri.EscapeDataString(acceptedAt)}"))
            .ShouldBe([deadLetteredEventId]);

        // First page, then its cursor, walks the newest-first order without repeating an Event.
        JsonElement firstPage = await GetAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1");
        firstPage.GetProperty("items")[0].GetProperty("event_id").GetGuid().ShouldBe(deadLetteredEventId);
        string cursor = firstPage.GetProperty("next_cursor").GetString()!;
        JsonElement secondPage = await GetAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&after={Uri.EscapeDataString(cursor)}");
        secondPage.GetProperty("items")[0].GetProperty("event_id").GetGuid().ShouldBe(olderEventId);
        secondPage.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?after=not-a-cursor");
        // A cursor issued for an unfiltered list does not carry over to a filtered one.
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?status=routed&after={Uri.EscapeDataString(cursor)}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?status=nonsense");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?delivery_status=failed");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?accepted_from=2026-01-02T00:00:00Z&accepted_to=2026-01-01T00:00:00Z");
    }

    [Fact]
    public async Task History_BreaksAcceptanceTimeTiesByEventIdAndScopesCursorsToTenantAndFilters()
    {
        // Three Events sharing one acceptance instant: only the id tie-breaker can order these, so
        // dropping it from either the ORDER BY or the keyset predicate repeats or skips a row here.
        var (seededEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        JsonElement seeded = (await GetAsync($"/admin/tenants/{fixture.TenantId}/events")).GetProperty("items")[0];
        seeded.GetProperty("event_id").GetGuid().ShouldBe(seededEventId);
        Guid sourceId = seeded.GetProperty("source_id").GetGuid();
        Guid topicId = seeded.GetProperty("topic_id").GetGuid();

        Guid[] tied = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        DateTimeOffset acceptedAt = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        foreach (Guid id in tied)
            await ExecuteAsync($$$"""
                INSERT INTO events (id, tenant_id, source_id, topic_id, event_type, payload, status, accepted_at)
                VALUES (@Id, @TenantId, @SourceId, @TopicId, 'tie.test',
                    {{{fixture.Json("@Payload")}}}, 'routed', @AcceptedAt);
                """,
                new
                {
                    Id = id,
                    fixture.TenantId,
                    SourceId = sourceId,
                    TopicId = topicId,
                    Payload = "{\"tie\":true}",
                    AcceptedAt = acceptedAt,
                });

        var walked = new List<Guid>();
        string? cursor = null;
        for (int page = 0; page < tied.Length; page++)
        {
            string url = $"/admin/tenants/{fixture.TenantId}/events?limit=1&accepted_to={Uri.EscapeDataString(acceptedAt.ToString("O"))}"
                + (cursor is null ? string.Empty : $"&after={Uri.EscapeDataString(cursor)}");
            JsonElement result = await GetAsync(url);
            walked.Add(result.GetProperty("items")[0].GetProperty("event_id").GetGuid());
            cursor = result.GetProperty("next_cursor").GetString();
        }

        // Each tied Event is visited exactly once: no repeat, no skip. The order within one acceptance
        // instant is deterministic per provider but not identical across them, because PostgreSQL and
        // SQL Server compare their native uuid types differently. Paging stays correct either way,
        // since the same comparison drives both the ORDER BY and the keyset predicate.
        walked.ShouldBe(tied.ToList(), ignoreOrder: true);
        walked.Distinct().Count().ShouldBe(tied.Length);

        // A cursor is bound to its tenant and to every active filter, so none of these may be reused.
        JsonElement firstPage = await GetAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1");
        string unfiltered = firstPage.GetProperty("next_cursor").GetString()!;
        string carried = Uri.EscapeDataString(unfiltered);
        await AssertBadRequestAsync($"/admin/tenants/{fixture.OtherTenantId}/events?limit=1&after={carried}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&delivery_status=succeeded&after={carried}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&source_id={Guid.NewGuid()}&after={carried}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&topic_id={Guid.NewGuid()}&after={carried}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&source_event_id=abc&after={carried}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&accepted_from=2026-01-01T00:00:00Z&after={carried}");
        await AssertBadRequestAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1&accepted_to=2027-01-01T00:00:00Z&after={carried}");
    }

    /// The cursor scope used to colon-join raw filter text with "all" as the missing-value sentinel,
    /// so a Source Event id that is itself "all" collided with the unfiltered scope, and one
    /// containing a delimiter or a newline could corrupt the cursor's own framing. Neither may
    /// happen: every distinct filter combination gets its own scope, and free-text values round-trip
    /// no matter what they contain. Equivalent timestamps also retain the same scope when written
    /// with a different UTC offset.
    [Fact]
    public async Task History_CursorScope_IsUnambiguousAndStableAcrossEquivalentTimestampOffsets()
    {
        var (firstAllId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (secondAllId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        foreach (Guid id in new[] { firstAllId, secondAllId })
            await ExecuteAsync(
                "UPDATE events SET source_event_id = @SourceEventId WHERE id = @Id",
                new { Id = id, SourceEventId = "all" });

        // A cursor issued for the unfiltered list and one issued for source_event_id=all must not
        // validate against each other, even though both filter sets once colon-joined to the same
        // "all" scope token.
        string unfilteredCursor = (await GetAsync($"/admin/tenants/{fixture.TenantId}/events?limit=1"))
            .GetProperty("next_cursor").GetString()!;
        string literalAllCursor = (await GetAsync($"/admin/tenants/{fixture.TenantId}/events?source_event_id=all&limit=1"))
            .GetProperty("next_cursor").GetString()!;

        await AssertBadRequestAsync(
            $"/admin/tenants/{fixture.TenantId}/events?source_event_id=all&limit=1&after={Uri.EscapeDataString(unfilteredCursor)}");
        await AssertBadRequestAsync(
            $"/admin/tenants/{fixture.TenantId}/events?limit=1&after={Uri.EscapeDataString(literalAllCursor)}");

        // Its own cursor still walks the source_event_id=all list correctly.
        JsonElement literalAllSecondPage = await GetAsync(
            $"/admin/tenants/{fixture.TenantId}/events?source_event_id=all&limit=1&after={Uri.EscapeDataString(literalAllCursor)}");
        literalAllSecondPage.GetProperty("items").GetArrayLength().ShouldBe(1);

        // A colon and an embedded newline in the filter text must not corrupt the scope's own
        // newline-delimited framing: the filtered list still issues a cursor and that cursor decodes.
        var (firstDelimitedId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (secondDelimitedId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        const string delimited = "a:b\nc";
        foreach (Guid id in new[] { firstDelimitedId, secondDelimitedId })
            await ExecuteAsync(
                "UPDATE events SET source_event_id = @SourceEventId WHERE id = @Id",
                new { Id = id, SourceEventId = delimited });

        JsonElement delimitedFirstPage = await GetAsync(
            $"/admin/tenants/{fixture.TenantId}/events?source_event_id={Uri.EscapeDataString(delimited)}&limit=1");
        string delimitedCursor = delimitedFirstPage.GetProperty("next_cursor").GetString()!;
        JsonElement delimitedSecondPage = await GetAsync(
            $"/admin/tenants/{fixture.TenantId}/events?source_event_id={Uri.EscapeDataString(delimited)}&limit=1&after={Uri.EscapeDataString(delimitedCursor)}");
        delimitedSecondPage.GetProperty("items").GetArrayLength().ShouldBe(1);
        delimitedSecondPage.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        // Equivalent DateTimeOffset values describe the same database filter even when the client
        // writes them with another offset, so changing only that representation keeps the cursor.
        const string acceptedFromUtc = "2026-01-01T00:00:00Z";
        const string acceptedToUtc = "2027-01-01T00:00:00Z";
        const string acceptedFromOffset = "2025-12-31T19:00:00-05:00";
        const string acceptedToOffset = "2026-12-31T19:00:00-05:00";
        JsonElement utcFirstPage = await GetAsync(
            $"/admin/tenants/{fixture.TenantId}/events?accepted_from={acceptedFromUtc}&accepted_to={acceptedToUtc}&limit=1");
        string utcCursor = utcFirstPage.GetProperty("next_cursor").GetString()!;
        JsonElement offsetSecondPage = await GetAsync(
            $"/admin/tenants/{fixture.TenantId}/events?accepted_from={Uri.EscapeDataString(acceptedFromOffset)}&accepted_to={Uri.EscapeDataString(acceptedToOffset)}&limit=1&after={Uri.EscapeDataString(utcCursor)}");
        offsetSecondPage.GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
    }

    private async Task AssertBadRequestAsync(string url) =>
        (await client.SendAsync(AdminRequest(HttpMethod.Get, url))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    private async Task<IReadOnlyList<Guid>> ListIdsAsync(string url) =>
        (await GetAsync(url)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("event_id").GetGuid()).ToList();

    private async Task<JsonElement> GetAsync(string url)
    {
        using HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Get, url));
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using DbConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }
}
