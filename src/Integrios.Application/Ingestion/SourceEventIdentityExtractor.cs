using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Ingestion;

internal static class SourceEventIdentityExtractor
{
    public static string ExtractWebhook(SourceEventIdentityRule rule, IReadOnlyDictionary<string, string> headers, JsonElement input) =>
        rule.Kind == "header"
            ? Required(headers.FirstOrDefault(pair => pair.Key.Equals(rule.Value, StringComparison.OrdinalIgnoreCase)).Value)
            : Required(ReadJsonPointer(input, rule.Value));

    public static string ExtractQueue(SourceEventIdentityRule rule, string? messageId, JsonElement input) =>
        rule.Kind == "message_id" ? Required(messageId) : Required(ReadJsonPointer(input, rule.Value));

    public static bool IsJsonPointer(string value)
    {
        if (value.Length < 2 || value[0] != '/')
            return false;

        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] != '~')
                continue;
            if (index + 1 == value.Length || value[index + 1] is not ('0' or '1'))
                return false;
            index++;
        }
        return true;
    }

    public static string IdempotencyKey(Guid sourceId, string sourceEventId) =>
        $"{sourceId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceEventId)))}";

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new EventAcceptanceException("Source Event identity could not be extracted.");

    private static string? ReadJsonPointer(JsonElement input, string pointer)
    {
        if (!IsJsonPointer(pointer))
            return null;

        JsonElement current = input;
        foreach (string encodedSegment in pointer[1..].Split('/'))
        {
            string segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out JsonElement property))
            {
                current = property;
                continue;
            }
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out int index)
                && index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }
            return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
