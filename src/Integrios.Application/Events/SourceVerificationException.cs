namespace Integrios.Application.Events;

public sealed class SourceVerificationException(string message) : Exception(message);
