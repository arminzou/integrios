using System.Text.Json;
using Integrios.Application.Events;
using Integrios.Tests.Shared;
using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using Integrios.Ingress.Endpoints;

namespace Integrios.Ingress.Tests;

public sealed class DomainModelTests
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

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "default-ingest-key",
            KeyPrefix = "intg_3f8a2c1",
            KeyHash = "hash",
            Status = OperationalStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(OperationalStatus.Active, tenant.Status);
        Assert.Equal(OperationalStatus.Active, apiKey.Status);
        Assert.Equal(tenant.Id, apiKey.TenantId);
    }
}
