using System.Text.Json;
using Integrios.Application.Ingestion;

namespace Integrios.Tests.Shared;

public sealed class FakeSourceVerifierRegistry(params ISourceVerifier[] verifiers) : ISourceVerifierRegistry
{
    private readonly IReadOnlyDictionary<string, ISourceVerifier> verifiersByScheme =
        verifiers.ToDictionary(verifier => verifier.Scheme, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ISourceVerifier> Registered => verifiersByScheme.Values.ToArray();

    public ISourceVerifier GetRequired(string scheme) =>
        TryGet(scheme, out ISourceVerifier verifier)
            ? verifier
            : throw new SourceVerificationException($"No source verifier registered for scheme '{scheme}'.");

    public bool TryGet(string scheme, out ISourceVerifier verifier) =>
        verifiersByScheme.TryGetValue(scheme, out verifier!);
}

public sealed class FakeHmacSha256SourceVerifier : ISourceVerifier
{
    public string Scheme => "hmac_sha256";
    public IReadOnlyList<string> RequiredConfigFields => [];
    public IReadOnlyList<string> RequiredSecretFields => ["secret"];

    public bool Verify(
        ReadOnlyMemory<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        JsonElement config,
        IReadOnlyDictionary<string, string> secrets) => true;
}
