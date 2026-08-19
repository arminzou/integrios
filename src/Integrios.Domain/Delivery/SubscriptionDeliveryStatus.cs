namespace Integrios.Domain.Delivery;

public enum SubscriptionDeliveryStatus
{
    Pending,
    InFlight,
    Succeeded,
    DeadLettered
}
