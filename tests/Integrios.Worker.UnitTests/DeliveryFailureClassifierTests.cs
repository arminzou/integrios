using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Worker.UnitTests;

public sealed class DeliveryFailureClassifierTests
{
    [Fact]
    public void IsTerminal_SucceededResult_IsFalse()
    {
        Assert.False(DeliveryFailureClassifier.IsTerminal(new DeliveryResult(true, 200)));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(0)] // transport/connection failure
    public void IsTerminal_TransientHttpSuccess_IsFalse(int statusCode)
    {
        var result = new DeliveryResult(false, statusCode, FailurePhase: DeliveryFailurePhase.Http);

        Assert.False(DeliveryFailureClassifier.IsTerminal(result));
    }

    [Fact]
    public void IsTerminal_Timeout_IsFalseEvenWithoutARetryableStatusCode()
    {
        var result = new DeliveryResult(false, 0, IsTimeout: true, FailurePhase: DeliveryFailurePhase.Http);

        Assert.False(DeliveryFailureClassifier.IsTerminal(result));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(200)] // a logically rejected 2xx from the outcome evaluator
    public void IsTerminal_OtherHttpSuccess_IsTrue(int statusCode)
    {
        var result = new DeliveryResult(false, statusCode, FailurePhase: DeliveryFailurePhase.Http);

        Assert.True(DeliveryFailureClassifier.IsTerminal(result));
    }

    [Theory]
    [InlineData(DeliveryFailurePhase.Transform)]
    [InlineData(DeliveryFailurePhase.SecretResolution)]
    [InlineData(DeliveryFailurePhase.RequestConstruction)]
    public void IsTerminal_NonHttpFailurePhase_IsFalseRegardlessOfStatusCode(DeliveryFailurePhase phase)
    {
        // These never reached a real HTTP outcome, so they keep the pre-existing
        // retry-until-exhaustion behavior rather than being reclassified as terminal.
        var result = new DeliveryResult(false, 404, FailurePhase: phase);

        Assert.False(DeliveryFailureClassifier.IsTerminal(result));
    }
}
