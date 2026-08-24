using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Worker.UnitTests;

public sealed class DeliveryExecutionVocabularyTests
{
    [Fact]
    public void DeliveryAttemptStatus_ContainsEveryDocumentedState()
    {
        Assert.Equal(
            [
                DeliveryAttemptStatus.InProgress,
                DeliveryAttemptStatus.Succeeded,
                DeliveryAttemptStatus.Failed,
                DeliveryAttemptStatus.Indeterminate
            ],
            Enum.GetValues<DeliveryAttemptStatus>());
    }

    [Fact]
    public void DeliveryFailurePhase_ContainsEveryDocumentedPhase()
    {
        Assert.Equal(
            [
                DeliveryFailurePhase.Transform,
                DeliveryFailurePhase.SecretResolution,
                DeliveryFailurePhase.RequestConstruction,
                DeliveryFailurePhase.Http
            ],
            Enum.GetValues<DeliveryFailurePhase>());
    }

    [Fact]
    public void DeliveryFinalizationStatus_DistinguishesOwnershipLossFromApplied()
    {
        Assert.NotEqual(
            DeliveryFinalizationStatus.Applied,
            DeliveryFinalizationStatus.OwnershipLost);
    }
}
