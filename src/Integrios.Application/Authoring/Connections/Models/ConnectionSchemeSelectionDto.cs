using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Connections;

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
