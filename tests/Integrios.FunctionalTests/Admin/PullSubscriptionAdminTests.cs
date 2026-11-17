using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Integrios.Admin.Endpoints;
using Integrios.Infrastructure.Connectors;

namespace Integrios.FunctionalTests.Admin;

public sealed class PullSubscriptionAdminTests(AdminApiFixture fixture) : SubscriptionAdminTestBase(fixture)
{
    private static readonly object HttpDelivery = new { version = 1, method = "POST", headers = new { }, body = "json" };
    private static readonly object HttpSuccess = new { evaluator = "json_boolean", field = "ok", expected = true };

    [Fact]
    public async Task PullSubscription_StoresNoHttpOperationAndRefusesOne()
    {
        AdminTopicResponse topic = await CreateTopicAsync("orders");
        Guid pullDestinationId = await CreatePullDestinationAsync();
        string subscriptions = $"/admin/tenants/{Fixture.TenantId}/topics/{topic.Id}/subscriptions";

        (await SendAsync(HttpMethod.Post, subscriptions, Body(pullDestinationId, HttpDelivery, null)))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, subscriptions, Body(pullDestinationId, null, HttpSuccess)))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        HttpResponseMessage created = await SendAsync(HttpMethod.Post, subscriptions, Body(pullDestinationId, null, null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement pull = await created.Content.ReadFromJsonAsync<JsonElement>();
        pull.GetProperty("http_delivery").ValueKind.ShouldBe(JsonValueKind.Null);
        Guid pullId = pull.GetProperty("id").GetGuid();
        (await StoredHttpDeliveryAsync(pullId)).ShouldBeNull();

        HttpResponseMessage updated = await SendAsync(
            HttpMethod.Put, $"{subscriptions}/{pullId}", Body(pullDestinationId, null, null));
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("http_delivery").ValueKind
            .ShouldBe(JsonValueKind.Null);
        (await StoredHttpDeliveryAsync(pullId)).ShouldBeNull();

        HttpResponseMessage push = await SendAsync(HttpMethod.Post, subscriptions, Body(Fixture.DestinationId, null, null));
        push.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await push.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("http_delivery").GetProperty("method")
            .GetString().ShouldBe("POST");
    }

    private static object Body(Guid destinationId, object? httpDelivery, object? httpSuccess) => new
    {
        name = "orders-service",
        event_types = new[] { "payment.created" },
        destination_id = destinationId,
        order_index = 0,
        mapping = (object?)null,
        http_delivery = httpDelivery,
        http_success = httpSuccess,
        description = (string?)null,
    };

    private async Task<Guid> CreatePullDestinationAsync()
    {
        HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{Fixture.TenantId}/destinations",
            new { connector_id = BuiltInConnectors.Subscriber.Id, name = "orders-service", configuration = new { } });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body) =>
        client.SendAsync(AdminRequest(method, path, body));

    private async Task<string?> StoredHttpDeliveryAsync(Guid subscriptionId)
    {
        await using var connection = Fixture.CreateConnection();
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<string?>(
            $"SELECT {Fixture.JsonText("http_delivery")} FROM subscriptions WHERE id = @Id", new { Id = subscriptionId });
    }
}
