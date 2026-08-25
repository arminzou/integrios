using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Domain.UnitTests;

public class EventStatusTests
{
    [Fact]
    public void EveryStatus_RoundTrips_ThroughDbValue()
    {
        foreach (var status in Enum.GetValues<EventStatus>())
            EventStatusMap.FromDbValue(EventStatusMap.ToDbValue(status)).ShouldBe(status);
    }

    [Theory]
    [InlineData(EventStatus.Accepted, "accepted")]
    [InlineData(EventStatus.Routed, "routed")]
    [InlineData(EventStatus.Unrouted, "unrouted")]
    [InlineData(EventStatus.DeadLettered, "dead_lettered")]
    public void DbValue_IsSnakeCase(EventStatus status, string expected)
        => EventStatusMap.ToDbValue(status).ShouldBe(expected);

    [Fact]
    public void JsonConverter_SerializesAsSnakeCaseString()
    {
        JsonSerializer.Serialize(EventStatus.Routed).ShouldBe("\"routed\"");
        JsonSerializer.Deserialize<EventStatus>("\"routed\"").ShouldBe(EventStatus.Routed);
    }

    [Fact]
    public void FromDbValue_Throws_OnUnknownStatus()
        => Should.Throw<ArgumentOutOfRangeException>(() => EventStatusMap.FromDbValue("completed"));
}
