using Integrios.Application.Delivery;

namespace Integrios.Worker.Tests;

public sealed class OutboxWorkerBackoffTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(10, 30 * 512)]
    public void CalculateBackoff_ReturnsExponentialDelay(int attemptCount, int expectedSeconds)
    {
        var policy = new RetryPolicy();
        var backoff = policy.CalculateBackoff(attemptCount);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), backoff);
    }

    [Fact]
    public void CalculateBackoff_CapsExponentAt10()
    {
        var policy = new RetryPolicy();
        var backoff11 = policy.CalculateBackoff(11);
        var backoff12 = policy.CalculateBackoff(12);
        Assert.Equal(backoff11, backoff12);
    }
}
