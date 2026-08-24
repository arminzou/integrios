using System.Text.Json;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connections;

public sealed record DestinationAuthenticationDto
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }

    public static DestinationAuthenticationDto? From(DestinationAuthentication? authentication) =>
        authentication is null
            ? null
            : new DestinationAuthenticationDto
            {
                Scheme = authentication.Scheme,
                Config = authentication.Config
            };
}
