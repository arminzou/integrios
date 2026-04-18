using System.Text.Json;
using Integrios.Core.Contracts;
using Integrios.Core.Domain.Common;
using Integrios.Core.Domain.Events;
using Integrios.Core.Domain.Tenants;

namespace Integrios.Api.Tests;

public sealed class InitialDomainModelTests
{
    [Fact]
    public void IngestEventRequest_RoundTrips_WithPayloadMetadataAndIdempotency()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = """
            {
              "sourceEventId": "evt_123",
              "eventType": "payment.created",
              "payload": {
                "paymentId": "pay_456",
                "amount": 1200
              },
              "metadata": {
                "traceId": "trace-1",
                "source": "demo-swiftpay"
              },
              "idempotencyKey": "idem-abc"
            }
            """;

        var request = JsonSerializer.Deserialize<IngestEventRequest>(json, jsonOptions);

        Assert.NotNull(request);
        Assert.Equal("evt_123", request.SourceEventId);
        Assert.Equal("payment.created", request.EventType);
        Assert.Equal("pay_456", request.Payload.GetProperty("paymentId").GetString());
        Assert.Equal(1200, request.Payload.GetProperty("amount").GetInt32());
        Assert.Equal("trace-1", request.Metadata?.GetProperty("traceId").GetString());
        Assert.Equal("idem-abc", request.IdempotencyKey);
    }

    [Fact]
    public void Event_CanRepresent_AcceptedInboundWork()
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        var payload = JsonDocument.Parse("""{"paymentId":"pay_456","amount":1200}""").RootElement.Clone();
        var metadata = JsonDocument.Parse("""{"source":"demo-swiftpay","traceId":"trace-1"}""").RootElement.Clone();

        var @event = new Event
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EventType = "payment.created",
            Payload = payload,
            Metadata = metadata,
            SourceEventId = "evt_123",
            IdempotencyKey = "idem-abc",
            Status = EventStatus.Accepted,
            AcceptedAt = acceptedAt
        };

        Assert.Equal(EventStatus.Accepted, @event.Status);
        Assert.Equal("payment.created", @event.EventType);
        Assert.Equal("pay_456", @event.Payload.GetProperty("paymentId").GetString());
        Assert.Equal("demo-swiftpay", @event.Metadata?.GetProperty("source").GetString());
        Assert.Equal("idem-abc", @event.IdempotencyKey);
    }

    [Fact]
    public void CoreEntities_UseExpectedV1Statuses()
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = "demo-swiftpay",
            Name = "Demo SwiftPay",
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var credential = new ApiCredential
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "default-ingest-key",
            KeyId = "key_123",
            SecretHash = "hash",
            Scopes = ["events.write"],
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(OperationalStatus.Active, tenant.Status);
        Assert.Equal(OperationalStatus.Active, credential.Status);
        Assert.Contains("events.write", credential.Scopes);
    }
}
