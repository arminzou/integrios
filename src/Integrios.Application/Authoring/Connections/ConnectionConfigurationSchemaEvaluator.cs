using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Integrios.Application.Authoring.Connections;

internal static class ConnectionConfigurationSchemaEvaluator
{
    public static void Validate(JsonElement value, JsonElement schema, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid($"{path} must be a JSON object.");

        JsonElement properties = schema.GetProperty("properties");
        HashSet<string> required = schema.TryGetProperty("required", out JsonElement requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

        foreach (string name in required)
        {
            if (!value.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
                throw Invalid($"{path} field '{name}' is required.");
        }

        bool allowAdditional = !schema.TryGetProperty("additionalProperties", out JsonElement additional)
            || additional.GetBoolean();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out JsonElement propertySchema))
            {
                if (!allowAdditional)
                    throw Invalid($"{path} field '{property.Name}' is not allowed.");
                continue;
            }

            ValidateScalar(property.Value, propertySchema, $"{path} field '{property.Name}'");
        }
    }

    private static void ValidateScalar(JsonElement value, JsonElement schema, string path)
    {
        string type = schema.GetProperty("type").GetString()!;
        bool typeMatches = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false,
        };
        if (!typeMatches)
            throw Invalid($"{path} must be a {type}.");

        if (schema.TryGetProperty("enum", out JsonElement allowed)
            && !allowed.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            throw Invalid($"{path} is not one of the allowed values.");
        }

        if (type == "string")
            ValidateString(value.GetString()!, schema, path);
        else if (type is "number" or "integer")
            ValidateNumber(value.GetDecimal(), schema, path);
    }

    private static void ValidateString(string value, JsonElement schema, string path)
    {
        int length = value.EnumerateRunes().Count();
        if (schema.TryGetProperty("minLength", out JsonElement minimum) && length < minimum.GetInt32())
            throw Invalid($"{path} is shorter than the allowed minimum.");
        if (schema.TryGetProperty("maxLength", out JsonElement maximum) && length > maximum.GetInt32())
            throw Invalid($"{path} is longer than the allowed maximum.");

        if (!schema.TryGetProperty("format", out JsonElement format))
            return;

        bool valid = format.GetString() switch
        {
            "uri" => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Scheme),
            "hostname" => Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6,
            _ => false,
        };
        if (!valid)
            throw Invalid($"{path} must use the declared {format.GetString()} format.");
    }

    private static void ValidateNumber(decimal value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("minimum", out JsonElement minimum)
            && value < decimal.Parse(minimum.GetRawText(), CultureInfo.InvariantCulture))
        {
            throw Invalid($"{path} is below the allowed minimum.");
        }
        if (schema.TryGetProperty("maximum", out JsonElement maximum)
            && value > decimal.Parse(maximum.GetRawText(), CultureInfo.InvariantCulture))
        {
            throw Invalid($"{path} is above the allowed maximum.");
        }
    }

    private static ConnectionConfigurationValidationException Invalid(string message) => new(message);
}

internal sealed class ConnectionConfigurationValidationException(string message) : Exception(message);
