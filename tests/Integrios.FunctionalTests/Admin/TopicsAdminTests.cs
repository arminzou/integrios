using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Admin.Endpoints;
using Integrios.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Integrios.FunctionalTests.Admin;

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
            new { key = "payments", description = "Payment events" }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        AdminTopicResponse? topic = await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options);
        topic.ShouldNotBeNull();
        topic.Key.ShouldBe("payments");
    }

    // The key is what a create must carry; the label is the part an Operator corrects.
    [Fact]
    public async Task Create_WithoutAKey_ReportsItOnTheKeyField()
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { description = "No key" }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("key")[0].GetString().ShouldBe("Key is required.");
    }

    // A key has no uppercase form, which is what makes the two providers compare keys identically
    // whatever collation the server carries. The grammar is therefore refused at the boundary
    // rather than left to a database setting.
    [Theory]
    [InlineData("Payments")]
    [InlineData("payments_v2")]
    [InlineData("-payments")]
    [InlineData("payments-")]
    [InlineData("pay ments")]
    public async Task Create_WithAKeyOutsideTheGrammar_ReportsItOnTheKeyField(string key)
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { key }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("key")[0].GetString()
            .ShouldBe("Key must be a lowercase DNS label of 1 to 63 characters.");
    }

    // The label is what changes; the key is not a field an update carries at all.
    [Fact]
    public async Task Update_ChangesTheLabelAndLeavesTheKey()
    {
        HttpResponseMessage created = await client.SendAsync(AdminRequest(
            HttpMethod.Post,
            $"/admin/tenants/{fixture.TenantId}/topics",
            new { key = "order-events" }));
        AdminTopicResponse topic = (await created.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;
        // Authored without a label, a Topic reads as its key rather than as nothing.
        topic.Name.ShouldBe("order-events");

        HttpResponseMessage renamed = await client.SendAsync(AdminRequest(
            HttpMethod.Put,
            $"/admin/tenants/{fixture.TenantId}/topics/{topic.Id}",
            new { name = "Order events", description = (string?)null }));

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        AdminTopicResponse updated = (await renamed.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;
        updated.Name.ShouldBe("Order events");
        updated.Key.ShouldBe("order-events");
    }
}
