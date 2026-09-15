using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Ingestion;

internal static class SourceEventIdentityExtractor
{
    public static string? ExtractWebhook(SourceEventIdentityRule rule, IReadOnlyDictionary<string, string> headers, JsonElement input) =>
        Extracted(rule, rule.Kind == "header"
            ? headers.FirstOrDefault(pair => pair.Key.Equals(rule.Value, StringComparison.OrdinalIgnoreCase)).Value
            : ReadJsonPointer(input, rule.Value));

    public static string? ExtractQueue(SourceEventIdentityRule rule, string? messageId, JsonElement input) =>
        Extracted(rule, rule.Kind == "message_id" ? messageId : ReadJsonPointer(input, rule.Value));

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

    // The prefix is the Source's immutable identifier and must stay that. Namespacing by anything
    // renameable — the Source label, a Connector key — means a rename recomputes every future key and
    // silently discards the deduplication history, readmitting every Event already seen. Per Source
    // rather than per Tenant because two Sources are two origins: the same provider event through
    // both is two Events by construction.
    public static string IdempotencyKey(Guid sourceId, string sourceEventId) =>
        $"{sourceId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceEventId)))}";

    // A rule that permits a missing value yields none, and the caller then falls through to whatever
    // identity the Source contract's mapping produced. That is the one asymmetry between the two
    // identity paths: the rule is immutable and read before the mapping, so only through the mapping
    // could an identity be absent without refusing the request. Permitting it here closes that gap
    // without moving the rule.
    private static string? Extracted(SourceEventIdentityRule rule, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        if (rule.AllowMissing)
            return null;
        throw new EventAcceptanceException("Source Event identity could not be extracted.");
    }

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
