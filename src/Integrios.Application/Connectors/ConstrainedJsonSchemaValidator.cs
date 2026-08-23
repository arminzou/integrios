using System.Text.Json;

namespace Integrios.Application.Connectors;

internal static class ConstrainedJsonSchemaValidator
{
    private static readonly HashSet<string> ObjectKeywords =
        ["type", "properties", "required", "additionalProperties"];
    private static readonly HashSet<string> StringKeywords =
        ["type", "enum", "format", "minLength", "maxLength"];
    private static readonly HashSet<string> NumericKeywords =
        ["type", "enum", "minimum", "maximum"];
    private static readonly HashSet<string> BooleanKeywords = ["type", "enum"];
    private static readonly HashSet<string> Formats = ["uri", "hostname"];

    public static void Validate(JsonElement schema, string path) => ValidateNode(schema, path, requireObject: true);

    private static void ValidateNode(JsonElement schema, string path, bool requireObject)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw Invalid($"{path} must be a JSON Schema object.");
        if (!schema.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String)
            throw Invalid($"{path}.type is required and must be a string.");

        string type = typeElement.GetString()!;
        if (requireObject && type != "object")
            throw Invalid($"{path}.type must be object.");
        if (!requireObject && type == "object")
            throw Invalid($"{path} nested object schemas are not supported.");

        IReadOnlySet<string> allowed = type switch
        {
            "object" => ObjectKeywords,
            "string" => StringKeywords,
            "number" or "integer" => NumericKeywords,
            "boolean" => BooleanKeywords,
            _ => throw Invalid($"{path}.type '{type}' is not supported."),
        };

        RejectUnknown(schema, allowed, path);
        switch (type)
        {
            case "object":
                ValidateObject(schema, path);
                break;
            case "string":
                ValidateString(schema, path);
                break;
            case "number":
            case "integer":
                ValidateNumber(schema, path, type == "integer");
                break;
            case "boolean":
                ValidateEnum(schema, path, JsonValueKind.True, JsonValueKind.False);
                break;
        }
    }

    private static void ValidateObject(JsonElement schema, string path)
    {
        if (!schema.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
            throw Invalid($"{path}.properties is required and must be an object.");

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
                throw Invalid($"{path}.properties cannot contain an empty name.");
            propertyNames.Add(property.Name);
            ValidateNode(property.Value, $"{path}.properties.{property.Name}", requireObject: false);
        }

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            if (required.ValueKind != JsonValueKind.Array)
                throw Invalid($"{path}.required must be an array.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || !seen.Add(item.GetString()!)
                    || !propertyNames.Contains(item.GetString()!))
                {
                    throw Invalid($"{path}.required must contain unique names declared in properties.");
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out JsonElement additionalProperties)
            && additionalProperties.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid($"{path}.additionalProperties must be a boolean.");
        }
    }

    private static void ValidateString(JsonElement schema, string path)
    {
        ValidateNonNegativeInteger(schema, "minLength", path);
        ValidateNonNegativeInteger(schema, "maxLength", path);
        if (schema.TryGetProperty("minLength", out JsonElement min)
            && schema.TryGetProperty("maxLength", out JsonElement max)
            && min.GetInt32() > max.GetInt32())
        {
            throw Invalid($"{path}.minLength cannot exceed maxLength.");
        }
        if (schema.TryGetProperty("format", out JsonElement format)
            && (format.ValueKind != JsonValueKind.String || !Formats.Contains(format.GetString()!)))
        {
            throw Invalid($"{path}.format must be uri or hostname.");
        }
        ValidateEnum(schema, path, JsonValueKind.String);
    }

    private static void ValidateNumber(JsonElement schema, string path, bool integer)
    {
        foreach (string keyword in new[] { "minimum", "maximum" })
        {
            if (schema.TryGetProperty(keyword, out JsonElement value)
                && (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetDecimal(out _)
                    || (integer && !value.TryGetInt64(out _))))
            {
                throw Invalid($"{path}.{keyword} must be a{(integer ? "n integer" : " number")}.");
            }
        }
        if (schema.TryGetProperty("minimum", out JsonElement min)
            && schema.TryGetProperty("maximum", out JsonElement max)
            && min.GetDecimal() > max.GetDecimal())
        {
            throw Invalid($"{path}.minimum cannot exceed maximum.");
        }
        ValidateEnum(schema, path, JsonValueKind.Number);
        if (integer && schema.TryGetProperty("enum", out JsonElement values)
            && values.EnumerateArray().Any(value => !value.TryGetInt64(out _)))
        {
            throw Invalid($"{path}.enum must contain only integers.");
        }
    }

    private static void ValidateEnum(JsonElement schema, string path, params JsonValueKind[] kinds)
    {
        if (!schema.TryGetProperty("enum", out JsonElement values))
            return;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            throw Invalid($"{path}.enum must be a non-empty array.");
        JsonElement[] enumValues = [.. values.EnumerateArray()];
        foreach (JsonElement value in enumValues)
        {
            if (!kinds.Contains(value.ValueKind)
                || (value.ValueKind == JsonValueKind.Number && !value.TryGetDecimal(out _)))
                throw Invalid($"{path}.enum contains a value incompatible with its type.");
        }
        for (int index = 0; index < enumValues.Length; index++)
        {
            if (enumValues[(index + 1)..].Any(value => JsonElement.DeepEquals(enumValues[index], value)))
                throw Invalid($"{path}.enum must contain unique values.");
        }
    }

    private static void ValidateNonNegativeInteger(JsonElement schema, string keyword, string path)
    {
        if (schema.TryGetProperty(keyword, out JsonElement value)
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < 0))
        {
            throw Invalid($"{path}.{keyword} must be a non-negative integer.");
        }
    }

    private static void RejectUnknown(JsonElement schema, IReadOnlySet<string> allowed, string path)
    {
        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw Invalid($"{path} contains unsupported JSON Schema keyword '{property.Name}'.");
        }
    }

    private static ConnectorManifestValidationException Invalid(string message) => new(message);
}
