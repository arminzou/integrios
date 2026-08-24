using System.Net;
using System.Net.Http.Json;
using Integrios.Admin.Endpoints;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.Application.FunctionalTests.Admin;

public sealed class TopicsAdminTests(AdminApiFixture fixture) : AdminApiTestBase, IClassFixture<AdminApiFixture>, IAsyncLifetime
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
    public async Task TopicAuthoring_DoesNotCreateOrReturnSourceAssociations()
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { name = "payments", description = "Payment events" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AdminTopicResponse? topic = await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options);
        Assert.NotNull(topic);
        Assert.Equal("payments", topic.Name);
    }
}
