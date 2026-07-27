namespace Integrios.Application.Events;

public sealed class EventAcceptanceException(string message) : Exception(message);
