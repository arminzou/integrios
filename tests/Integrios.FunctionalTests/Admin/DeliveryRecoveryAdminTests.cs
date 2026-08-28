using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task OperatorKey_CanInspectAndReplayOneDeadLetteredDelivery()
    {
        var (eventId, deliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();
        string route = $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries";

        HttpResponseMessage historyResponse = await client.SendAsync(AdminRequest(HttpMethod.Get, route));
        historyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        EventDto? history = await historyResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        history.ShouldNotBeNull();
        EventDeliveryDto delivery = history.EventDeliveries.ShouldHaveSingleItem();
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
