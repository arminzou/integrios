using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
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
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();
        var configuration = new { };
        var request = new { connector_id = connectorId, topic_id = topicId, type = "webhook", configuration };

        HttpResponseMessage create = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", request));
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        SourceDto source = (await create.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        source.Configuration.TryGetProperty("callback_id", out _).ShouldBeTrue();

        SourceListDto listed = (await (await client.SendAsync(AdminRequest(HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/sources", null))).Content.ReadFromJsonAsync<SourceListDto>(HostJson.Options))!;
        listed.Items.ShouldHaveSingleItem().Id.ShouldBe(source.Id);

        HttpResponseMessage update = await client.SendAsync(AdminRequest(HttpMethod.Put, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", FullSourceUpdate(configuration)));
        SourceDto updated = (await update.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        updated.Configuration.GetProperty("callback_id").GetString().ShouldBe(source.Configuration.GetProperty("callback_id").GetString());

        (await client.SendAsync(AdminRequest(HttpMethod.Delete, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(AdminRequest(HttpMethod.Put, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", FullSourceUpdate(configuration)))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Intake resolves one revision per request and an accepted Event is never remapped, so the
    // revision is what separates traffic normalized under the old contract from traffic normalized
    // under the new one. An edit that leaves it behind makes that boundary unobservable.
    [Fact]
    public async Task NormalizationEdit_AdvancesTheSourceRevision()
    {
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage create = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "webhook",
            configuration = new { },
            input_requirements = new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } },
            mapping = new { engine = "jsonata", version = "1", expression = "{ \"event_type\": \"probe.created\", \"payload\": $ }" },
        }));
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        SourceDto source = (await create.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;

        HttpResponseMessage update = await client.SendAsync(AdminRequest(HttpMethod.Put, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", new
        {
            configuration = new { },
            verification = (object?)null,
            input_requirements = new { type = "object", properties = new { id = new { type = "string" }, kind = new { type = "string" } }, required = new[] { "id" } },
            mapping = new { engine = "jsonata", version = "1", expression = "{ \"event_type\": \"probe.updated\", \"payload\": $ }" },
        }));
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        SourceDto updated = (await update.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;

        updated.Revision.ShouldNotBe(source.Revision);
    }

    [Fact]
    public async Task SourceAuthoring_CreatesEventApiAndQueueSources()
    {
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage eventApi = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "event_api",
            configuration = new { }
        }));
        HttpResponseMessage queue = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "queue",
            configuration = new { transport = "azure_service_bus", authentication = new { scheme = "azure_identity" }, transport_config = new { @namespace = "example.servicebus.windows.net", queue_name = "events" } }
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
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "queue",
            configuration = new
            {
                transport = "azure_service_bus",
                authentication = new { scheme, secret_ref = secretReference },
                transport_config = new { @namespace = "example.servicebus.windows.net", queue_name = "events" },
            }
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // azure_identity draws its credential from the ambient chain, so a secret reference alongside it
    // is dead configuration that reads as if a secret were in use.
    [Fact]
    public async Task QueueSourceAuthoring_RejectsSecretReferenceOnAzureIdentity()
    {
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "queue",
            configuration = new
            {
                transport = "azure_service_bus",
                authentication = new { scheme = "azure_identity", secret_ref = "sb_connection_string" },
                transport_config = new { @namespace = "example.servicebus.windows.net", queue_name = "events" },
            }
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // azure_identity has nothing but the namespace to locate the broker, so a bare name that the
    // connection_string path would harmlessly ignore becomes a connect-time failure with no
    // authoring-time signal. Not enforced for connection_string, where the host comes from the
    // secret and the field is informational.
    [Theory]
    [InlineData("sb-integrios")]
    [InlineData("https://sb-integrios.servicebus.windows.net")]
    [InlineData("sb-integrios.servicebus.windows.net/queues")]
    public async Task QueueSourceAuthoring_RejectsNonHostNamespaceForAzureIdentity(string ns)
    {
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "queue",
            configuration = new
            {
                transport = "azure_service_bus",
                authentication = new { scheme = "azure_identity" },
                transport_config = new { @namespace = ns, queue_name = "events" },
            }
        }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // A Service Bus topic is consumed through one of its subscriptions, which behaves exactly like a
    // queue. The keys are prefixed because a bare topic_name inside a Source configuration would read
    // as the Integrios Topic the Source publishes to, which is a different thing entirely.
    [Fact]
    public async Task QueueSourceAuthoring_AcceptsTopicSubscriptionForm()
    {
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "queue",
            configuration = new
            {
                transport = "azure_service_bus",
                authentication = new { scheme = "azure_identity" },
                transport_config = new
                {
                    @namespace = "example.servicebus.windows.net",
                    topic_name = "orders",
                    subscription_name = "integrios",
                },
            }
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    // both entity forms at once
    [InlineData("events", "orders", "integrios")]
    // neither
    [InlineData(null, null, null)]
    // topic without its subscription, and a subscription without its topic
    [InlineData(null, "orders", null)]
    [InlineData(null, null, "integrios")]
    public async Task QueueSourceAuthoring_RequiresExactlyOneEntityForm(
        string? queueName,
        string? topicName,
        string? subscriptionName)
    {
        Guid connectorId = await CreateSourceConnectorAsync();
        Guid topicId = await CreateTopicAsync();

        var transportConfig = new Dictionary<string, object?>
        {
            ["namespace"] = "example.servicebus.windows.net",
        };
        if (queueName is not null)
            transportConfig["queue_name"] = queueName;
        if (topicName is not null)
            transportConfig["topic_name"] = topicName;
        if (subscriptionName is not null)
            transportConfig["subscription_name"] = subscriptionName;

        var configuration = new Dictionary<string, object?>
        {
            ["transport"] = "azure_service_bus",
            ["authentication"] = new { scheme = "azure_identity" },
            ["transport_config"] = transportConfig,
        };

        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "queue",
            configuration,
        }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // The Source's verification is the webhook's whole defence, and an edit that only means to
    // change the mapping must carry it back untouched. Before the reference names were returned,
    // the only expressible body was a null that a stored-value fallback quietly reinterpreted.
    [Fact]
    public async Task AVerifiedSource_CanBeResubmittedUnchanged()
    {
        Guid connectorId = await fixture.ApplyConnectorManifestAsync(
            "verified_probe",
            TestConnectorManifest.Create(
                "verified_probe", "Verified probe", "source", sourceVerificationSchemes: ["hmac_sha256"]));
        Guid topicId = await CreateTopicAsync();

        HttpResponseMessage create = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/sources", new
        {
            connector_id = connectorId,
            topic_id = topicId,
            type = "webhook",
            configuration = new { },
            verification = new { scheme = "hmac_sha256", config = new { }, secret_refs = new { secret = "probe_signing_secret" } },
        }));
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        SourceDto source = (await create.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        source.Verification.ShouldNotBeNull();

        JsonElement read = await (await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}"))).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement verification = read.GetProperty("verification");

        HttpResponseMessage update = await client.SendAsync(AdminRequest(HttpMethod.Put, $"/admin/tenants/{fixture.TenantId}/sources/{source.Id}", new
        {
            configuration = read.GetProperty("configuration"),
            verification = new
            {
                scheme = verification.GetProperty("scheme").GetString(),
                config = verification.GetProperty("config"),
                secret_refs = verification.GetProperty("secret_refs"),
            },
            input_requirements = (object?)null,
            mapping = (object?)null,
        }));

        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        SourceDto updated = (await update.Content.ReadFromJsonAsync<SourceDto>(HostJson.Options))!;
        updated.Verification.ShouldNotBeNull();
        updated.Verification.SecretRefs.GetProperty("secret").GetString().ShouldBe("probe_signing_secret");
    }

    // An update replaces the whole Source, so every field travels even when only one changes.
    private static object FullSourceUpdate(object configuration) => new
    {
        configuration,
        verification = (object?)null,
        input_requirements = (object?)null,
        mapping = (object?)null,
    };

    private async Task<Guid> CreateTopicAsync()
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{fixture.TenantId}/topics", new { name = "source-topic" }));
        return (await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!.Id;
    }

    private async Task<Guid> CreateSourceConnectorAsync()
    {
        using JsonDocument document = JsonDocument.Parse(TestConnectorManifest.Create("source_test", "Source test", "source", declarativeSourceContract: true));
        HttpResponseMessage connectorResponse = await client.SendAsync(AdminRequest(HttpMethod.Put, "/admin/connectors/source_test/versions/1", document.RootElement));
        ConnectorDto connector = (await connectorResponse.Content.ReadFromJsonAsync<ConnectorDto>(HostJson.Options))!;
        return connector.Id;
    }
}
