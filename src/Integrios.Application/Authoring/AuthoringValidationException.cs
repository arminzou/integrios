namespace Integrios.Application.Authoring;

public abstract class AuthoringValidationException(
    string message,
    string field = "",
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Field { get; } = field;
}
