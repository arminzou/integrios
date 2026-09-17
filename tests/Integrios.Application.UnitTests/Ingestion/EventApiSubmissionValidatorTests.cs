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

    // Event types are freeform within a floor. What fails silently or damages what displays it is
    // refused on every intake path, since Source mappings and Event API submissions share this check.
    [Theory]
    [InlineData("payment.created ", "whitespace")]
    [InlineData(" payment.created", "whitespace")]
    [InlineData("payment\tcreated", "control characters")]
    [InlineData("payment.created\n", "whitespace")]
    [InlineData("payment\ncreated", "control characters")]
    public void EventTypeBelowTheFloor_IsRejected(string eventType, string reason)
    {
        EventAcceptanceException exception = Should.Throw<EventAcceptanceException>(() =>
            EventApiSubmissionValidator.Validate(Submission(eventType)));

        exception.Message.ShouldContain(reason);
    }

    [Fact]
    public void EventTypeLongerThanTheCap_IsRejected()
    {
        EventAcceptanceException exception = Should.Throw<EventAcceptanceException>(() =>
            EventApiSubmissionValidator.Validate(Submission(new string('a', 201))));

        exception.Message.ShouldContain("200 characters");
    }

    // Providers name their events as they like, and Integrios takes the names as they come.
    [Theory]
    [InlineData("shopify.orders/create")]
    [InlineData("gitlab.Push Hook")]
    [InlineData("github.pull_request.synchronize")]
    public void ProviderEventTypes_AreAcceptedAsTheyCome(string eventType)
    {
        EventApiSubmissionValidator.Validate(Submission(eventType)).EventType.ShouldBe(eventType);
    }

    [Fact]
    public void EventTypeAtTheCap_IsAccepted()
    {
        string eventType = new('a', 200);

        EventApiSubmissionValidator.Validate(Submission(eventType)).EventType.ShouldBe(eventType);
    }

    private static JsonElement Submission(string eventType) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["event_type"] = eventType, ["payload"] = new { } });
}
