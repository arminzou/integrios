namespace Integrios.Subscriber.UnitTests;

public sealed class DeliveryResultTests
{
    [Fact]
    public void AConsumerCanReadTheOutcomeItsHandlerChose()
    {
        DeliveryResult.Ack.IsRetry.ShouldBeFalse();
        DeliveryResult.Ack.RetryAfter.ShouldBeNull();

        DeliveryResult retry = DeliveryResult.Retry(TimeSpan.FromSeconds(1.2), "throttled");
        retry.IsRetry.ShouldBeTrue();
        retry.RetryAfter.ShouldBe(TimeSpan.FromSeconds(2));
        retry.Reason.ShouldBe("throttled");

        DeliveryResult.Retry().RetryAfter.ShouldBeNull();
    }
}
