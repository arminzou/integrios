using System.Text.Json;
using Integrios.Application.Ingestion;
using Integrios.Application.Transforms;

namespace Integrios.Application.UnitTests;

public sealed class EventApiSubmissionValidatorTests
{
    [Fact]
    public void ValidFixedEventApiShape_IsAcceptedWithoutSourceMapping()
    {
        var input = JsonSerializer.Deserialize<JsonElement>("""
            {"event_type":"payment.created","source_event_id":"evt-42","payload":{"amount":42},"metadata":{"origin":"erp"}}
            """);

        SourceContractOutput output = EventApiSubmissionValidator.Validate(input);

        output.EventType.ShouldBe("payment.created");
        output.SourceEventId.ShouldBe("evt-42");
        output.Payload.GetProperty("amount").GetInt32().ShouldBe(42);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"event_type\":\"payment.created\"}")]
    [InlineData("{\"event_type\":\"payment.created\",\"source_event_id\":\"\",\"payload\":{}}")]
    public void InvalidFixedEventApiShape_IsRejected(string json)
    {
        EventAcceptanceException exception = Should.Throw<EventAcceptanceException>(() =>
            EventApiSubmissionValidator.Validate(JsonSerializer.Deserialize<JsonElement>(json)));

        exception.Message.ShouldNotBeEmpty();
    }
}
