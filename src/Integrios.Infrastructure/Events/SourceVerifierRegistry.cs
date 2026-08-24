using Integrios.Application.Ingestion;

namespace Integrios.Infrastructure.Events;

internal sealed class SourceVerifierRegistry(IEnumerable<ISourceVerifier> verifiers) : ISourceVerifierRegistry
{
    private readonly Dictionary<string, ISourceVerifier> verifiersByScheme =
        verifiers.ToDictionary(verifier => verifier.Scheme, StringComparer.OrdinalIgnoreCase);

    public ISourceVerifier GetRequired(string scheme)
    {
        if (TryGet(scheme, out ISourceVerifier verifier))
            return verifier;

        throw new SourceVerificationException($"Unknown source verification scheme '{scheme}'.");
    }

    public bool TryGet(string scheme, out ISourceVerifier verifier) =>
        verifiersByScheme.TryGetValue(scheme, out verifier!);
}
