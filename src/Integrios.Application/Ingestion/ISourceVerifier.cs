using System.Text.Json;

namespace Integrios.Application.Ingestion;

// Mirrors IDestinationAuthenticator/IDestinationAuthenticatorRegistry on the inbound side: a
// declarative, Connector-selected scheme that verifies a webhook request rather than authenticates
// an outbound one.
public interface ISourceVerifier
{
    string Scheme { get; }
    IReadOnlyList<string> RequiredConfigFields { get; }
    IReadOnlyList<string> RequiredSecretFields { get; }

    bool Verify(
        ReadOnlyMemory<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secrets);

    // Withheld from the Source mapping and identity rule, so a mapping cannot copy a credential into an Event.
    IReadOnlyList<string> CredentialHeaders(JsonElement config) => [];
}

public interface ISourceVerifierRegistry
{
    IReadOnlyCollection<ISourceVerifier> Registered { get; }
    ISourceVerifier GetRequired(string scheme);
    bool TryGet(string scheme, out ISourceVerifier verifier);
}
