namespace Integrios.Domain.Events;

public enum EventStatus
{
    Accepted = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    DeadLettered = 4
}
