using System.Text.Json;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connections;

public sealed record SourceVerificationDto
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }

    public static SourceVerificationDto? From(SourceVerification? verification) =>
        verification is null
            ? null
            : new SourceVerificationDto
            {
                Scheme = verification.Scheme,
                Config = verification.Config
            };
}
