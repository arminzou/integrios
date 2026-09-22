namespace Integrios.Domain.Enums;

public enum DeliveryFailurePhase
{
    Transform = 0,
    SecretResolution = 1,
    Authentication = 2,
    RequestConstruction = 3,
    Http = 4
}
