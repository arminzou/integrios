using System.Text.Json;
using Integrios.Application.Common;

namespace Integrios.Infrastructure.Events;

internal static class SourceVerificationConfig
{
    public static string? HeaderNameError(JsonElement config) =>
        OptionalString(config, "header_name", out string? value) is { } error
            ? error
            : value is not null && !HttpHeaderName.IsValid(value)
                ? "Source verification config field 'header_name' must be a valid HTTP header name."
                : null;

    public static string? StringError(JsonElement config, string field) => OptionalString(config, field, out _);

    // An absent or null field means the scheme's default.
    public static string? OptionalString(JsonElement config, string field, out string? value)
    {
        value = null;
        if (!config.TryGetProperty(field, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String)
            return $"Source verification config field '{field}' must be a string.";
        value = element.GetString();
        return null;
    }
}
