namespace Integrios.Domain.Delivery;

public enum DeliveryFailurePhase
{
    Transform = 0,
    SecretResolution = 1,
    RequestConstruction = 2,
    Http = 3
}
