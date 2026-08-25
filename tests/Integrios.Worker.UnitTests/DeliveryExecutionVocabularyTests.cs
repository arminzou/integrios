using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Worker.UnitTests;

public sealed class DeliveryExecutionVocabularyTests
{
    [Fact]
    public void DeliveryAttemptStatus_ContainsEveryDocumentedState()
    {
        Enum.GetValues<DeliveryAttemptStatus>().ShouldBe(
            [
                DeliveryAttemptStatus.InProgress,
                DeliveryAttemptStatus.Succeeded,
                DeliveryAttemptStatus.Failed,
                DeliveryAttemptStatus.Indeterminate
            ]);
    }

    [Fact]
    public void DeliveryFailurePhase_ContainsEveryDocumentedPhase()
    {
        Enum.GetValues<DeliveryFailurePhase>().ShouldBe(
            [
                DeliveryFailurePhase.Transform,
                DeliveryFailurePhase.SecretResolution,
                DeliveryFailurePhase.RequestConstruction,
                DeliveryFailurePhase.Http
            ]);
    }

    [Fact]
    public void DeliveryFinalizationStatus_DistinguishesOwnershipLossFromApplied()
    {
        DeliveryFinalizationStatus.Applied.ShouldNotBe(
            DeliveryFinalizationStatus.OwnershipLost);
    }
}
