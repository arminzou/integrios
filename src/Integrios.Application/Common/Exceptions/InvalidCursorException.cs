namespace Integrios.Application.Common.Exceptions;

public sealed class InvalidCursorException : Exception
{
    public InvalidCursorException() : base("The cursor is invalid for this list.")
    {
    }
}
