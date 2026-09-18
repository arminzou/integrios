namespace Integrios.Application.Authoring;

/// A write the request is well-formed for but live configuration refuses: another resource depends on
/// what it would remove or disagrees with what it would add. Answered with 409 and never resolved by
/// changing the other resource on the caller's behalf.
public sealed class AuthoringConflictException(string message) : Exception(message);
