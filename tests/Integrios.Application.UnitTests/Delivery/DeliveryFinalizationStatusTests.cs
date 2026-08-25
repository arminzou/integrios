using Integrios.Application.Delivery;

namespace Integrios.Application.UnitTests;

public sealed class DeliveryFinalizationStatusTests
{
    [Fact]
    public void DeliveryFinalizationStatus_DistinguishesOwnershipLossFromApplied()
    {
        DeliveryFinalizationStatus.Applied.ShouldNotBe(
            DeliveryFinalizationStatus.OwnershipLost);
    }
}
