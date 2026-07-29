using Integrios.Application.Secrets;

namespace Integrios.Infrastructure.Secrets;

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
