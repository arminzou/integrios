using System.Text.Json;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Sources;

public sealed record SourceVerificationDto
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }

    /// The logical names verification resolves its secrets by, never the values. Ingestion holds
    /// the only mount that can turn one into a secret, so returning the name discloses nothing and
    /// is what lets a client resubmit a Source it did not change.
    public required JsonElement SecretRefs { get; init; }

    public static SourceVerificationDto? From(SourceVerification? verification) =>
        verification is null
            ? null
            : new SourceVerificationDto
            {
                Scheme = verification.Scheme,
                Config = verification.Config,
                SecretRefs = verification.SecretRefs
            };
}
