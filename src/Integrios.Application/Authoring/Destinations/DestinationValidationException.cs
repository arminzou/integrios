namespace Integrios.Application.Authoring.Destinations;

public sealed class DestinationValidationException(string message, string field = "")
    : AuthoringValidationException(message, field);
