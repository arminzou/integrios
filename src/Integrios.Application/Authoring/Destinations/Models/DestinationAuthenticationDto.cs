using System.Text.Json;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Destinations;

public sealed record DestinationAuthenticationDto
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }

    /// The logical names an authentication scheme resolves its credentials by, never the values.
    /// A reference names a slot in a secret store the control plane cannot read, so returning it
    /// discloses nothing and is what lets a client resubmit a Destination it did not change.
    public required JsonElement SecretRefs { get; init; }

    public static DestinationAuthenticationDto? From(DestinationAuthentication? authentication) =>
        authentication is null
            ? null
            : new DestinationAuthenticationDto
            {
                Scheme = authentication.Scheme,
                Config = authentication.Config,
                SecretRefs = authentication.SecretRefs,
            };
}
