using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class DeliveryExecutionOptionsTests
{
    [Fact]
    public void Defaults_MatchFencedLeaseTimingContract()
    {
        DeliveryExecutionOptions options = DeliveryExecutionOptions.Default;

        options.HttpTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.AttemptDeadline.ShouldBe(TimeSpan.FromSeconds(45));
        options.LeaseDuration.ShouldBe(TimeSpan.FromMinutes(2));
        options.ShutdownGracePeriod.ShouldBe(TimeSpan.FromSeconds(60));
        options.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(30));
        options.RetryMaxAttempts.ShouldBe(3);
        options.Validate();
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validate_RejectsUnsafeTimingRelationships(DeliveryExecutionOptions options, string expectedSetting)
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(options.Validate);

        exception.Message.ShouldContain(expectedSetting, Case.Sensitive);
    }

    public static TheoryData<DeliveryExecutionOptions, string> InvalidOptions => new()
    {
        { new(TimeSpan.Zero, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60)), "HttpTimeout" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60)), "AttemptDeadline" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60)), "LeaseDuration" },
        { new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(45)), "ShutdownGracePeriod" },
        { Valid with { RetryBaseDelay = TimeSpan.Zero }, "Retry:BaseDelay" },
        { Valid with { RetryMaxAttempts = 0 }, "Retry:MaxAttempts" }
    };

    private static DeliveryExecutionOptions Valid => DeliveryExecutionOptions.Default;
}
