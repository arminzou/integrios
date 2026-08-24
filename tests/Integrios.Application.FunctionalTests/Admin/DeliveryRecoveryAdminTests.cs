using System.Net;
using System.Net.Http.Json;
using Integrios.Application.Ingestion;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Application.FunctionalTests.Admin;

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
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        EventDto? history = await historyResponse.Content.ReadFromJsonAsync<EventDto>(HostJson.Options);
        Assert.NotNull(history);
        EventDeliveryDto delivery = Assert.Single(history.EventDeliveries);
        Assert.Equal(deliveryId, delivery.EventDeliveryId);
        Assert.Equal("dead_lettered", delivery.Status);
        Assert.Equal("failed", Assert.Single(history.DeliveryAttempts).Status);

        HttpResponseMessage replayResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"{route}/{deliveryId}/replay"));
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        Assert.Equal(route, replayResponse.Headers.Location?.ToString());
        Assert.Equal("pending", await fixture.GetDeliveryStatusAsync(deliveryId));

        HttpResponseMessage repeatedReplay = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"{route}/{deliveryId}/replay"));
        Assert.Equal(HttpStatusCode.Conflict, repeatedReplay.StatusCode);
    }

    [Fact]
    public async Task Replay_DeliveryFromAnotherEvent_Returns404()
    {
        var (eventId, _) = await fixture.SeedDeadLetteredDeliveryAsync();
        var (_, otherDeliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/events/{eventId}/deliveries/{otherDeliveryId}/replay"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("dead_lettered", await fixture.GetDeliveryStatusAsync(otherDeliveryId));
    }

    [Fact]
    public async Task Replay_OtherTenantEvent_Returns404()
    {
        var (eventId, deliveryId) = await fixture.SeedDeadLetteredDeliveryAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.OtherTenantId}/events/{eventId}/deliveries/{deliveryId}/replay"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("dead_lettered", await fixture.GetDeliveryStatusAsync(deliveryId));
    }
}
