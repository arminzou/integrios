using Integrios.Application.Abstractions.Auth;

namespace Integrios.Infrastructure.Http.Auth;

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
