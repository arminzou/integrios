namespace Integrios.Application.Topics;

public sealed class TopicRequestValidationException(string message) : Exception(message);
