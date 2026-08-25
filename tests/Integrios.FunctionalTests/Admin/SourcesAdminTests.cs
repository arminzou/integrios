using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Connections;
using Integrios.Admin.Endpoints;
using Integrios.Application.Authoring.Sources;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

public sealed class SourcesAdminTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
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
    public async Task SourceLifecycle_CreatesListsUpdatesAndPermanentlyRevokesWebhook()
    {
        Guid connectionId = await CreateSourceConnectionAsync();
        Guid topicId = await CreateTopicAsync();
        var configuration = new { source_contract = "event_json" };
        var request = new { connection_id = connectionId, topic_id = topicId, type = "webhook", configuration };

        HttpResponseMessage create = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", request));
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        SourceDto source = (await create.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        source.Configuration.TryGetProperty("callback_id", out _).ShouldBeTrue();

        SourceListDto listed = (await (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/sources", null))).Content.ReadFromJsonAsync<SourceListDto>(HostJson.Options))!;
        listed.Items.ShouldHaveSingleItem().Id.ShouldBe(source.Id);

        HttpResponseMessage update = await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", new { configuration }));
        SourceDto updated = (await update.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        updated.Configuration.GetProperty("callback_id").GetString().ShouldBe(source.Configuration.GetProperty("callback_id").GetString());

        (await client.SendAsync(AdminRequest(HttpMethod.Delete, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(AdminRequest(HttpMethod.Patch, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", new { configuration }))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SourceAuthoring_CreatesEventApiAndQueueSources()
    {
        Guid connectionId = await CreateSourceConnectionAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage eventApi = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connection_id = connectionId,
            topic_id = topicId,
            type = "event_api",
            configuration = new { source_contract = "event_json" }
        }));
        HttpResponseMessage queue = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connection_id = connectionId,
            topic_id = topicId,
            type = "queue",
            configuration = new { source_contract = "event_json", transport = "azure_service_bus", @namespace = "example.servicebus.windows.net", queue_name = "events", authentication = new { scheme = "azure_identity" } }
        }));

        eventApi.StatusCode.ShouldBe(HttpStatusCode.Created);
        queue.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // Queue authentication the receiver cannot build a client for must fail the authoring call.
    // Left to Ingestion it surfaces at host startup instead, where a single unusable Source stops
    // the whole data plane from starting.
    [Theory]
    [InlineData("entra_id", null)]
    [InlineData("connection_string", null)]
    public async Task QueueSourceAuthoring_RejectsAuthenticationTheReceiverCannotUse(
        string scheme,
        string? secretReference)
    {
        Guid connectionId = await CreateSourceConnectionAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connection_id = connectionId,
            topic_id = topicId,
            type = "queue",
            configuration = new
            {
                source_contract = "event_json",
                transport = "azure_service_bus",
                @namespace = "example.servicebus.windows.net",
                queue_name = "events",
                authentication = new { scheme, secret_ref = secretReference },
            }
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // azure_identity draws its credential from the ambient chain, so a secret reference alongside it
    // is dead configuration that reads as if a secret were in use.
    [Fact]
    public async Task QueueSourceAuthoring_RejectsSecretReferenceOnAzureIdentity()
    {
        Guid connectionId = await CreateSourceConnectionAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connection_id = connectionId,
            topic_id = topicId,
            type = "queue",
            configuration = new
            {
                source_contract = "event_json",
                transport = "azure_service_bus",
                @namespace = "example.servicebus.windows.net",
                queue_name = "events",
                authentication = new { scheme = "azure_identity", secret_ref = "sb_connection_string" },
            }
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private async Task<Guid> CreateTopicAsync()
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/topics", new { name = "source-topic" }));
        return (await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!.Id;
    }

    private async Task<Guid> CreateSourceConnectionAsync()
    {
        using JsonDocument document = JsonDocument.Parse(TestConnectorManifest.Create("source_test", "Source test", "source", declarativeSourceContract: true));
        HttpResponseMessage connectorResponse = await client.SendAsync(AdminRequest(HttpMethod.Put, "/admin/connectors/source_test/versions/1", document.RootElement));
        ConnectorDto connector = (await connectorResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        HttpResponseMessage connectionResponse = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/connections",
            new { connector_id = connector.Id, name = "source-test", config = new { }, source_verification = (object?)null, destination_authentication = (object?)null }));
        return (await connectionResponse.Content.ReadFromJsonAsync<ConnectionDto>(HostJson.Options))!.Id;
    }
}
