namespace Integrios.Domain.Delivery;

public enum DeliveryAttemptStatus
{
    Pending = 0,
    InProgress = 1,
    Succeeded = 2,
    Failed = 3,
    DeadLettered = 4
}
