using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Integrios.Client.UnitTests;

public sealed class DeliveryTransportTests
{
    private const string ClaimJson = """
        {
          "deliveries": [
            {
              "delivery_id": "057615d5-d3a1-4ff3-a19b-e17fac5f3539",
              "lease_token": "test-lease",
              "attempt_number": 2,
              "event_id": "8a2ccdb5-dc5d-454b-b488-13b93fa3959b",
              "event_type": "order.placed",
              "topic": "orders",
              "accepted_at": "2026-11-24T14:30:00Z",
              "payload": { "orderId": 42 },
              "lease_expires_at": "2026-11-24T14:31:00Z",
              "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
            }
          ]
        }
        """;

    [Fact]
    public async Task ClaimDeserializesEveryDeliveryField()
    {
        using var client = new HttpClient(new ClaimResponseHandler(ClaimJson)) { BaseAddress = new Uri("https://example.test/") };
        var transport = new HttpDeliveryTransport(client);

        var deliveries = await transport.ClaimAsync(1, 0, CancellationToken.None);

        Delivery delivery = deliveries.ShouldHaveSingleItem();
        delivery.DeliveryId.ShouldBe(Guid.Parse("057615d5-d3a1-4ff3-a19b-e17fac5f3539"));
        delivery.LeaseToken.ShouldBe("test-lease");
        delivery.AttemptNumber.ShouldBe(2);
        delivery.EventId.ShouldBe(Guid.Parse("8a2ccdb5-dc5d-454b-b488-13b93fa3959b"));
        delivery.EventType.ShouldBe("order.placed");
        delivery.Topic.ShouldBe("orders");
        delivery.AcceptedAt.ShouldBe(new DateTimeOffset(2026, 11, 24, 14, 30, 0, TimeSpan.Zero));
        delivery.Payload.GetProperty("orderId").GetInt32().ShouldBe(42);
        delivery.LeaseExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 24, 14, 31, 0, TimeSpan.Zero));
        delivery.Traceparent.ShouldBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    }

    [Fact]
    public async Task ClaimRejectsDeliveryWithoutAnId()
    {
        JsonNode claim = JsonNode.Parse(ClaimJson)!;
        claim["deliveries"]![0]!.AsObject().Remove("delivery_id");
        using var client = new HttpClient(new ClaimResponseHandler(claim.ToJsonString())) { BaseAddress = new Uri("https://example.test/") };
        var transport = new HttpDeliveryTransport(client);

        await Assert.ThrowsAsync<JsonException>(() => transport.ClaimAsync(1, 0, CancellationToken.None));
    }

    private sealed class ClaimResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
