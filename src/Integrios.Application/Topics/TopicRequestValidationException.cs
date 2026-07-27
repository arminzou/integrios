namespace Integrios.Application.Topics;

public sealed class TopicRequestValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
