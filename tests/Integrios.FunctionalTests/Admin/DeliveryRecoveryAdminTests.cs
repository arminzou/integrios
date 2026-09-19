using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Application.Authoring.Tenants;
using Integrios.Application.Delivery;
using Integrios.Application.Ingestion;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class DeliveryRecoveryAdminTests : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
{
    private readonly AdminApiFixture fixture;
    private HttpClient client = null!;

    public DeliveryRecoveryAdminTests(AdminApiFixture fixture) => this.fixture = fixture;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        client = fixture.WebFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync()
    {
        client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OperatorKey_ReadsWhatWasSentAndWhatCameBack()
    {
        var (eventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        EventDiagnosticsDto? diagnostics =
            await response.Content.ReadFromJsonAsync<EventDiagnosticsDto>(HostJson.Options);
        diagnostics.ShouldNotBeNull();

        diagnostics.Payload.ShouldNotBeNull();
        diagnostics.Payload!.Value.GetProperty("recovery").GetBoolean().ShouldBeTrue();

        // The detail names the Topic and type an unrouted Event would need a Subscription for, the
        // same Topic the ledger row carries.
        JsonElement ledger = await (await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/events"))).Content.ReadFromJsonAsync<JsonElement>();
        Guid ledgerTopicId = ledger.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("event_id").GetGuid() == eventId).GetProperty("topic_id").GetGuid();
        diagnostics.TopicId.ShouldBe(ledgerTopicId);
        diagnostics.EventType.ShouldBe("recovery.test");

        DeliveryAttemptDiagnosticsDto attempt = diagnostics.DeliveryAttempts.ShouldHaveSingleItem();
        attempt.RequestPayload.ShouldNotBeNull();
        attempt.RequestPayload!.Value.GetProperty("sent").GetString().ShouldBe("body");
        attempt.ResponseBody.ShouldBe(AdminApiFixture.SeededResponseBody);
        attempt.ResponseStatusCode.ShouldBe(503);
        attempt.ResponseBodyTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_RetainsHistoricalNamesAndScrubsRuntimeConfiguration()
    {
        var (eventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        ProvenanceRow resources = await connection.QuerySingleAsync<ProvenanceRow>(
            $"""
            SELECT e.source_id AS SourceId, e.topic_id AS TopicId,
                ed.subscription_id AS SubscriptionId, ed.destination_id AS DestinationId,
                s.name AS SourceName, t.name AS TopicName, t.{fixture.KeyColumn} AS TopicKey,
                sub.name AS SubscriptionName, d.name AS DestinationName
            FROM events e
            JOIN event_deliveries ed ON ed.event_id = e.id
            JOIN sources s ON s.id = e.source_id
            JOIN topics t ON t.id = e.topic_id
            JOIN subscriptions sub ON sub.id = ed.subscription_id
            JOIN destinations d ON d.id = ed.destination_id
            WHERE e.id = @EventId
            """,
            new { EventId = eventId });

        (await Delete($"/admin/tenants/{fixture.TenantId}/topics/{resources.TopicId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Delete($"/admin/tenants/{fixture.TenantId}/destinations/{resources.DestinationId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Delete($"/admin/tenants/{fixture.TenantId}/sources/{resources.SourceId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await Delete($"/admin/tenants/{fixture.TenantId}/topics/{resources.TopicId}/subscriptions/{resources.SubscriptionId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Delete($"/admin/tenants/{fixture.TenantId}/sources/{resources.SourceId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Delete($"/admin/tenants/{fixture.TenantId}/topics/{resources.TopicId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Delete($"/admin/tenants/{fixture.TenantId}/destinations/{resources.DestinationId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        foreach (string path in new[]
        {
            $"/admin/tenants/{fixture.TenantId}/sources/{resources.SourceId}",
            $"/admin/tenants/{fixture.TenantId}/topics/{resources.TopicId}",
            $"/admin/tenants/{fixture.TenantId}/topics/{resources.TopicId}/subscriptions/{resources.SubscriptionId}",
            $"/admin/tenants/{fixture.TenantId}/destinations/{resources.DestinationId}",
        })
        {
            (await client.SendAsync(AdminRequest(HttpMethod.Get, path))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        (await client.SendAsync(AdminRequest(
                HttpMethod.Post,
                $"/admin/tenants/{fixture.TenantId}/topics",
                new { key = resources.TopicKey })))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        EventDiagnosticsDto history = (await (await client.SendAsync(AdminRequest(
                HttpMethod.Get,
                $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries")))
            .Content.ReadFromJsonAsync<EventDiagnosticsDto>(HostJson.Options))!;
        history.SourceName.ShouldBe(resources.SourceName);
        history.SourceDeleted.ShouldBeTrue();
        history.TopicKey.ShouldBe(resources.TopicKey);
        history.TopicName.ShouldBe(resources.TopicName);
        history.TopicDeleted.ShouldBeTrue();
        EventDeliveryDiagnosticsDto delivery = history.EventDeliveries.ShouldHaveSingleItem();
        delivery.SubscriptionName.ShouldBe(resources.SubscriptionName);
        delivery.SubscriptionDeleted.ShouldBeTrue();
        delivery.DestinationName.ShouldBe(resources.DestinationName);
        delivery.DestinationDeleted.ShouldBeTrue();

        TombstoneRow tombstones = await connection.QuerySingleAsync<TombstoneRow>($"""
            SELECT {fixture.JsonText("s.configuration")} AS SourceConfiguration,
                {fixture.JsonText("s.event_types")} AS SourceEventTypes,
                s.revision AS SourceRevision,
                {fixture.JsonText("sub.event_types")} AS SubscriptionEventTypes,
                {fixture.JsonText("sub.http_delivery")} AS SubscriptionHttpDelivery,
                {fixture.JsonText("d.configuration")} AS DestinationConfiguration,
                CASE WHEN s.deleted_at IS NULL THEN 0 ELSE 1 END AS SourceDeleted,
                CASE WHEN sub.deleted_at IS NULL THEN 0 ELSE 1 END AS SubscriptionDeleted,
                CASE WHEN t.deleted_at IS NULL THEN 0 ELSE 1 END AS TopicDeleted,
                CASE WHEN d.deleted_at IS NULL THEN 0 ELSE 1 END AS DestinationDeleted
            FROM sources s
            JOIN topics t ON t.id = s.topic_id
            JOIN subscriptions sub ON sub.topic_id = t.id
            JOIN destinations d ON d.id = sub.destination_id
            WHERE s.id = @SourceId
            """, resources);
        tombstones.SourceConfiguration.ShouldBe("{}");
        tombstones.SourceEventTypes.ShouldBe("[]");
        tombstones.SourceRevision.ShouldBeEmpty();
        tombstones.SubscriptionEventTypes.ShouldBe("[]");
        tombstones.SubscriptionHttpDelivery.ShouldBeNull();
        tombstones.DestinationConfiguration.ShouldBe("{}");
        tombstones.SourceDeleted.ShouldBe(1);
        tombstones.SubscriptionDeleted.ShouldBe(1);
        tombstones.TopicDeleted.ShouldBe(1);
        tombstones.DestinationDeleted.ShouldBe(1);
    }

    private Task<HttpResponseMessage> Delete(string path) =>
        client.SendAsync(AdminRequest(HttpMethod.Delete, path));

    // The Overview's tiles. Counted from what the seed actually creates, so a count that starts
    // reporting a neighbouring Tenant's rows fails here rather than on screen.
    [Fact]
    public async Task TenantOverview_CountsOnlyThisTenantsConfiguration()
    {
        await fixture.SeedDeadLetteredDeliveryAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/overview"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        TenantOverviewDto? overview = await response.Content.ReadFromJsonAsync<TenantOverviewDto>(HostJson.Options);
        overview.ShouldNotBeNull();
        overview.Topics.ShouldBe(1);
        overview.Sources.ShouldBe(1);
        overview.Subscriptions.ShouldBe(1);
        // The fixture seeds one Destination and the dead-letter setup adds another.
        overview.Destinations.ShouldBe(2);
        overview.IngestionEndpoint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TenantOverview_IsNotFoundForATenantThatDoesNotExist()
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{Guid.NewGuid()}/overview"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Criterion 4. Bodies belong to the one Event a reader asked for, never to a page of rows: a
    // ledger that carried them would put them through every log, cache and history entry that a
    // list request touches.
    [Fact]
    public async Task EventList_NeverReturnsPayloadOrDeliveryBodies()
    {
        await fixture.SeedDeadLetteredDeliveryAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/events"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        foreach (string forbidden in new[] { "payload", "metadata", "request_payload", "response_body", "response_body_truncated" })
            PropertyNames(document.RootElement).ShouldNotContain(forbidden);
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (string nested in PropertyNames(property.Value))
                        yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    foreach (string nested in PropertyNames(item))
                        yield return nested;
                break;
        }
    }

    private sealed record ProvenanceRow(
        Guid SourceId,
        Guid TopicId,
        Guid SubscriptionId,
        Guid DestinationId,
        string SourceName,
        string TopicName,
        string TopicKey,
        string SubscriptionName,
        string DestinationName);

    private sealed record TombstoneRow(
        string SourceConfiguration,
        string SourceEventTypes,
        string SourceRevision,
        string SubscriptionEventTypes,
        string? SubscriptionHttpDelivery,
        string DestinationConfiguration,
        int SourceDeleted,
        int SubscriptionDeleted,
        int TopicDeleted,
        int DestinationDeleted);

    [Fact]
    public async Task OperatorKey_CanInspectAndReplayOneDeadLetteredDelivery()
    {
        var (eventId, deliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();
        string route = $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries";

        HttpResponseMessage historyResponse = await client.SendAsync(AdminRequest(HttpMethod.Get, route));
        historyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        EventDiagnosticsDto? history = await historyResponse.Content.ReadFromJsonAsync<EventDiagnosticsDto>(HostJson.Options);
        history.ShouldNotBeNull();
        EventDeliveryDiagnosticsDto delivery = history.EventDeliveries.ShouldHaveSingleItem();
        delivery.EventDeliveryId.ShouldBe(deliveryId);
        delivery.Status.ShouldBe("dead_lettered");
        history.DeliveryAttempts.ShouldHaveSingleItem().Status.ShouldBe("failed");

        HttpResponseMessage replayResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"{route}/{deliveryId}/replay"));
        replayResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        replayResponse.Headers.Location?.ToString().ShouldBe(route);
        (await fixture.GetDeliveryStatusAsync(deliveryId)).ShouldBe("pending");

        HttpResponseMessage repeatedReplay = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"{route}/{deliveryId}/replay"));
        repeatedReplay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task EventDetail_ProjectsOnlyAValidOutboxRootTraceId()
    {
        var (eventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        string route = $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries";
        const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await fixture.AddOutboxTraceparentAsync(eventId, traceparent);
        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Get, route));
        string content = await response.Content.ReadAsStringAsync();
        EventDto detail = JsonSerializer.Deserialize<EventDto>(content, HostJson.Options).ShouldBeOfType<EventDto>();

        detail.TraceId.ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        content.ShouldNotContain("traceparent");

        await fixture.ResetAsync();
        var (missingEventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        HttpResponseMessage missingResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/events/{missingEventId}/deliveries"));
        (await missingResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options)).ShouldNotBeNull().TraceId.ShouldBeNull();

        await fixture.AddOutboxTraceparentAsync(missingEventId, "not-a-traceparent");
        HttpResponseMessage malformedResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/events/{missingEventId}/deliveries"));
        (await malformedResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options)).ShouldNotBeNull().TraceId.ShouldBeNull();
    }

    [Fact]
    public async Task Replay_DeliveryFromAnotherEvent_Returns404()
    {
        var (eventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (_, otherDeliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries/{otherDeliveryId}/replay"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.GetDeliveryStatusAsync(otherDeliveryId)).ShouldBe("dead_lettered");
    }

    [Fact]
    public async Task Replay_OtherTenantEvent_Returns404()
    {
        var (eventId, deliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.OtherTenantId}/events/{eventId}/deliveries/{deliveryId}/replay"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.GetDeliveryStatusAsync(deliveryId)).ShouldBe("dead_lettered");
    }
}
