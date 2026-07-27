namespace Integrios.Application.Common.Exceptions;

public sealed class DuplicateResourceException(string message, Exception? innerException = null)
    : Exception(message, innerException);
