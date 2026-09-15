namespace Integrios.Application.Secrets;

// A named secret that did not resolve: absent, empty, or not usable as a secret value. Public and in
// Application rather than beside the resolvers that throw it, because a host has to map it to a
// response and cannot catch a type it cannot see.
//
// The reference is carried for logging, never for a response body. It is a non-secret name, but it
// is an internal naming convention and an external caller has no business learning it.
public sealed class SecretResolutionException : Exception
{
    public SecretResolutionException(string secretReference, string providerName)
        : base($"Secret '{SafeReference(secretReference)}' could not be resolved using provider '{providerName}'.")
    {
        SecretReference = SafeReference(secretReference);
        ProviderName = providerName;
    }

    public string SecretReference { get; }
    public string ProviderName { get; }

    private static string SafeReference(string secretReference) =>
        SecretReferenceName.IsValid(secretReference) ? secretReference : "invalid";
}
