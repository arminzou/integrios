using Integrios.Domain.Enums;

namespace Integrios.Domain.UnitTests;

public sealed class DeliveryStatusVocabularyTests
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
}
