namespace Integrios.Domain.Enums;

public enum DeliveryFailurePhase
{
    Transform = 0,
    SecretResolution = 1,
    RequestConstruction = 2,
    Http = 3
}
