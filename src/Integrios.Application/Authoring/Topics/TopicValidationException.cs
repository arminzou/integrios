namespace Integrios.Application.Authoring.Topics;

public sealed class TopicValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
