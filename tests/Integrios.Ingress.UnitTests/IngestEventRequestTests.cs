using System.Text.Json;
using Integrios.Tests.Shared;
using Integrios.Ingress.Endpoints;

namespace Integrios.Ingress.UnitTests;

public sealed class IngestEventRequestTests
{
    [Fact]
    public void IngestEventRequest_RoundTrips_WithPayloadMetadataAndIdempotency()
    {
        // Envelope keys are snake_case by host policy; payload and metadata are passthrough
        // JsonElements, so their producer's own casing survives untouched.
        var json = """
            {
              "source_event_id": "evt_123",
              "source_connection_id": "6fd3608d-b34b-4cf8-a5fd-401c8d95f149",
              "topic_name": "payments",
              "event_type": "payment.created",
              "payload": {
                "paymentId": "pay_456",
                "amount": 1200
              },
              "metadata": {
                "traceId": "trace-1",
                "source": "demo-swiftpay"
              },
              "idempotency_key": "idem-abc"
            }
            """;

        var request = JsonSerializer.Deserialize<IngestEventRequest>(json, HostJson.Options);

        Assert.NotNull(request);
        Assert.Equal("evt_123", request.SourceEventId);
        Assert.Equal(Guid.Parse("6fd3608d-b34b-4cf8-a5fd-401c8d95f149"), request.SourceConnectionId);
        Assert.Equal("payment.created", request.EventType);
        Assert.Equal("pay_456", request.Payload.GetProperty("paymentId").GetString());
        Assert.Equal(1200, request.Payload.GetProperty("amount").GetInt32());
        Assert.Equal("trace-1", request.Metadata?.GetProperty("traceId").GetString());
        Assert.Equal("idem-abc", request.IdempotencyKey);
    }
}
