using System.Text.Json;
using Integrios.Domain.Events;

namespace Integrios.Ingress.Tests;

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
    [InlineData(EventStatus.FannedOut, "fanned_out")]
    [InlineData(EventStatus.Unrouted, "unrouted")]
    [InlineData(EventStatus.DeadLettered, "dead_lettered")]
    public void DbValue_IsSnakeCase(EventStatus status, string expected)
        => Assert.Equal(expected, EventStatusMap.ToDbValue(status));

    [Fact]
    public void JsonConverter_SerializesAsSnakeCaseString()
    {
        Assert.Equal("\"fanned_out\"", JsonSerializer.Serialize(EventStatus.FannedOut));
        Assert.Equal(EventStatus.FannedOut, JsonSerializer.Deserialize<EventStatus>("\"fanned_out\""));
    }

    [Fact]
    public void FromDbValue_Throws_OnUnknownStatus()
        => Assert.Throws<ArgumentOutOfRangeException>(() => EventStatusMap.FromDbValue("completed"));
}
