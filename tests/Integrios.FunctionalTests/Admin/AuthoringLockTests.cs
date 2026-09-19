using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integrios.Admin.Endpoints;
using Integrios.Application.Authoring;
using Integrios.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Integrios.FunctionalTests.Admin;

// Authoring operations that read the Topic's union and then write against it serialize per Topic. These
// pin the lease itself and the race it exists for: selecting a type while its last declaration is being
// withdrawn. Topic deletion and Source deletion take the same lease over the same code path.
public sealed class AuthoringLockTests(AdminApiFixture fixture) : SubscriptionAdminTestBase(fixture)
{
    [Fact]
    public async Task Lease_ExcludesTheSameResourceUntilItIsDisposed()
    {
        using IServiceScope scope = Fixture.WebFactory.Services.CreateScope();
        var authoringLock = scope.ServiceProvider.GetRequiredService<IAuthoringLock>();
        Guid id = Guid.NewGuid();

        IAsyncDisposable lease = await authoringLock.AcquireAsync(
            AuthoringResource.Topic, [id], CancellationToken.None);
        await Should.ThrowAsync<AuthoringLockConflictException>(
            () => authoringLock.AcquireAsync(AuthoringResource.Topic, [id], CancellationToken.None));
        // The same identifier under another resource kind is a different lock.
        await (await authoringLock.AcquireAsync(AuthoringResource.Destination, [id], CancellationToken.None))
            .DisposeAsync();

        await lease.DisposeAsync();
        await (await authoringLock.AcquireAsync(AuthoringResource.Topic, [id], CancellationToken.None))
            .DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentAuthoring_NeverLeavesASubscriptionSelectingAnUndeclaredType()
    {
        AdminTopicResponse topic = await CreateBareTopicAsync("orders");
        Guid source = await CreatedIdAsync(await CreateSourceAsync(topic.Id, "order.created", "order.shipped"));

        HttpResponseMessage[] responses = await Task.WhenAll(
            UpdateSourceAsync(source, "order.created"),
            SelectSubscriptionAsync(topic.Id, "order.shipped"));

        responses.ShouldContain(response => response.IsSuccessStatusCode);
        IReadOnlyList<string> union = (await GetTopicAsync(topic.Id)).EventTypes;
        if (responses[1].IsSuccessStatusCode)
            (await SelectedEventTypesAsync(responses[1])).ShouldBeSubsetOf(union);
    }

    private async Task<AdminTopicResponse> CreateBareTopicAsync(string key)
    {
        HttpResponseMessage response = await client.SendAsync(AdminRequest(
            HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/topics", new { key }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;
    }

    private async Task<AdminTopicResponse> GetTopicAsync(Guid topicId) =>
        (await (await client.SendAsync(AdminRequest(
            HttpMethod.Get, $"/admin/tenants/{Fixture.TenantId}/topics/{topicId}")))
            .Content.ReadFromJsonAsync<AdminTopicResponse>(HostJson.Options))!;

    private static async Task<IReadOnlyList<string>> SelectedEventTypesAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.GetProperty("event_types").EnumerateArray().Select(value => value.GetString()!)];
    }

    private Task<HttpResponseMessage> CreateSourceAsync(Guid topicId, params string[] eventTypes) =>
        client.SendAsync(AdminRequest(HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/sources", new
        {
            connector_id = Fixture.HttpConnectorId,
            topic_id = topicId,
            name = $"intake-{Guid.NewGuid():N}",
            type = "event_api",
            event_types = eventTypes,
            configuration = new { },
        }));

    private Task<HttpResponseMessage> UpdateSourceAsync(Guid sourceId, params string[] eventTypes) =>
        client.SendAsync(AdminRequest(HttpMethod.Put, $"/admin/tenants/{Fixture.TenantId}/sources/{sourceId}", new
        {
            name = "intake",
            configuration = new { },
            verification = (object?)null,
            input_requirements = (object?)null,
            mapping = (object?)null,
            event_identity_rule = (object?)null,
            event_types = eventTypes,
        }));

    private Task<HttpResponseMessage> SelectSubscriptionAsync(Guid topicId, params string[] eventTypes) =>
        client.SendAsync(AdminRequest(
            HttpMethod.Post, $"/admin/tenants/{Fixture.TenantId}/topics/{topicId}/subscriptions", new
            {
                name = "orders-to-erp",
                event_types = eventTypes,
                destination_id = Fixture.DestinationId,
                order_index = 0,
            }));

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }
}
