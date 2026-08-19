using System.Text.Json;
using Integrios.Domain.Connections;

namespace Integrios.Infrastructure.Data;

internal static class ModelConfigurationConversions
{
    public static string SerializeJson<T>(T value) =>
        JsonSerializer.Serialize(value, ConnectionSchemeSelection.StoredJson);

    public static T DeserializeJson<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, ConnectionSchemeSelection.StoredJson)
        ?? throw new JsonException($"Could not deserialize {typeof(T).Name}.");

    public static string ToSnakeCase<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static TEnum FromSnakeCase<TEnum>(string value) where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Single(candidate =>
            string.Equals(ToSnakeCase(candidate), value, StringComparison.Ordinal));
}
