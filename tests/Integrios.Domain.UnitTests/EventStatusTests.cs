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
            Assert.Equal(status, EventStatusMap.FromDbValue(EventStatusMap.ToDbValue(status)));
    }

    [Theory]
    [InlineData(EventStatus.Accepted, "accepted")]
    [InlineData(EventStatus.Routed, "routed")]
    [InlineData(EventStatus.Unrouted, "unrouted")]
    [InlineData(EventStatus.DeadLettered, "dead_lettered")]
    public void DbValue_IsSnakeCase(EventStatus status, string expected)
        => Assert.Equal(expected, EventStatusMap.ToDbValue(status));

    [Fact]
    public void JsonConverter_SerializesAsSnakeCaseString()
    {
        Assert.Equal("\"routed\"", JsonSerializer.Serialize(EventStatus.Routed));
        Assert.Equal(EventStatus.Routed, JsonSerializer.Deserialize<EventStatus>("\"routed\""));
    }

    [Fact]
    public void FromDbValue_Throws_OnUnknownStatus()
        => Assert.Throws<ArgumentOutOfRangeException>(() => EventStatusMap.FromDbValue("completed"));
}
