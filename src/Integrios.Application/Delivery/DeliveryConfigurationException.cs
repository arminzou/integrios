namespace Integrios.Application.Delivery;

// Marks a delivery preparation failure whose message is Operator-readable and carries no secret
// value, so it can be persisted on the DeliveryAttempt and logged verbatim. Messages from any other
// exception are replaced, because framework header validation embeds the offending value.
public sealed class DeliveryConfigurationException(string message) : Exception(message)
{
    public const string GenericFailureMessage = "Delivery request could not be constructed.";

    public static string SafeMessage(Exception exception, string? fallback = null) =>
        exception is DeliveryConfigurationException
            ? exception.Message
            : fallback ?? GenericFailureMessage;
}
