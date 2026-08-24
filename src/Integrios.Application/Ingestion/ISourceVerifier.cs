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
}

public interface ISourceVerifierRegistry
{
    ISourceVerifier GetRequired(string scheme);
    bool TryGet(string scheme, out ISourceVerifier verifier);
}
