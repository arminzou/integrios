extern alias IngestionHost;
using System.Net;
using System.Net.Http.Json;
using Integrios.Application.Ingestion;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Ingestion;

// A Tenant is the top of the intake fence: deactivating it must stop every Source type, not only the
// Event API path that authenticates against the Tenant directly. These prove webhook resolution, the
// broker reconciliation read, and Event acceptance all see the Tenant's status, and that the data
// plane never rewrites a Source's own status to achieve it.
public sealed class TenantIntakeFenceTests : IClassFixture<PostgresApiFixture>, IAsyncLifetime
{
    private readonly PostgresApiFixture fixture;
    private readonly string tenantAAuthHeaderValue = $"Bearer {PostgresApiFixture.TenantAToken}";
    private HttpClient client = null!;
    private Guid topicId;
    private Guid connectorId;

    public TenantIntakeFenceTests(PostgresApiFixture fixture)
    {
        this.fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDataAsync();
        connectorId = await fixture.SeedSourceConnectorAsync(fixture.TenantAId, "fence-source");
        topicId = await fixture.SeedTopicAsync(fixture.TenantAId, "fence-topic");
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
    public async Task DeactivatingTenant_RefusesWebhookIntake_AndReactivatingResumes()
    {
        Guid callbackId = await fixture.CreateWebhookSourceAsync(
            fixture.TenantAId, connectorId, topicId, "[\"github.push\"]");

        using HttpResponseMessage accepted = await PostWebhookAsync(callbackId);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await fixture.ChangeTenantStatusAsync(fixture.TenantAId, "inactive");

        using HttpResponseMessage refused = await PostWebhookAsync(callbackId);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.GetEventCountAsync()).ShouldBe(1);

        await fixture.ChangeTenantStatusAsync(fixture.TenantAId, "active");

        using HttpResponseMessage resumed = await PostWebhookAsync(callbackId);
        resumed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await fixture.GetEventCountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task DeactivatingTenant_RefusesEventApiIntake_AndReactivatingResumes()
    {
        Guid sourceId = await fixture.CreateEventApiSourceAsync(fixture.TenantAId, connectorId, topicId);

        using HttpResponseMessage accepted = await PostEventApiAsync(sourceId);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await fixture.ChangeTenantStatusAsync(fixture.TenantAId, "inactive");

        using HttpResponseMessage refused = await PostEventApiAsync(sourceId);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await fixture.ChangeTenantStatusAsync(fixture.TenantAId, "active");

        using HttpResponseMessage resumed = await PostEventApiAsync(sourceId);
        resumed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DeactivatingTenant_RemovesItsBrokerSourceFromReconciliation()
    {
        Guid sourceId = await fixture.CreateBrokerSourceAsync(fixture.TenantAId, connectorId, topicId);
        IBrokerSourceReader reader = fixture.WebFactory.Services.GetRequiredService<IBrokerSourceReader>();

        (await reader.ListActiveAzureServiceBusSourcesAsync(CancellationToken.None))
            .ShouldContain(source => source.SourceId == sourceId);

        await fixture.ChangeTenantStatusAsync(fixture.TenantAId, "inactive");

        (await reader.ListActiveAzureServiceBusSourcesAsync(CancellationToken.None))
            .ShouldNotContain(source => source.SourceId == sourceId);

        await fixture.ChangeTenantStatusAsync(fixture.TenantAId, "active");

        (await reader.ListActiveAzureServiceBusSourcesAsync(CancellationToken.None))
            .ShouldContain(source => source.SourceId == sourceId);
    }

    private Task<HttpResponseMessage> PostWebhookAsync(Guid callbackId) =>
        client.PostAsync(
            $"/webhooks/{callbackId}",
            JsonContent.Create(new { event_type = "github.push", payload = new { @ref = "refs/heads/main" } }));

    private Task<HttpResponseMessage> PostEventApiAsync(Guid sourceId)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/events?source_id={sourceId}")
        {
            Content = JsonContent.Create(new { event_type = "payment.created", payload = new { amount = 1200 } })
        };
        message.Headers.TryAddWithoutValidation("Authorization", tenantAAuthHeaderValue);
        return client.SendAsync(message);
    }
}
