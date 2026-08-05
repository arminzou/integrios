using System.Text.Json;
using Integrios.Domain.Integrations;

namespace Integrios.Application.Connections;

public sealed record ConnectionSchemeSelectionDto
{
    public required string Scheme { get; init; }
    public required JsonElement Config { get; init; }

    public static ConnectionSchemeSelectionDto? From(ConnectionSchemeSelection? selection) =>
        selection is null
            ? null
            : new ConnectionSchemeSelectionDto
            {
                Scheme = selection.Scheme,
                Config = selection.Config
            };
}
