using System.Linq.Expressions;
using System.Text.Json;
using Integrios.Domain.Connections;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Integrios.Infrastructure.Data;

internal sealed class StoredJsonConverter<T>()
    : ValueConverter<T, string>(ToProviderExpression, FromProviderExpression)
{
    private static readonly Expression<Func<T, string>> ToProviderExpression =
        value => JsonSerializer.Serialize(value, ConnectionSchemeSelection.StoredJson);

    private static readonly Expression<Func<string, T>> FromProviderExpression =
        value => Deserialize(value);

    private static T Deserialize(string value) =>
        JsonSerializer.Deserialize<T>(value, ConnectionSchemeSelection.StoredJson)
        ?? throw new JsonException($"Could not deserialize {typeof(T).Name}.");
}

internal sealed class JsonElementStoredConverter()
    : ValueConverter<JsonElement, string>(
        value => value.GetRawText(),
        value => DeserializeElement(value))
{
    private static JsonElement DeserializeElement(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();
}

internal sealed class NullableJsonElementStoredConverter()
    : ValueConverter<JsonElement?, string?>(
        value => value.HasValue ? value.Value.GetRawText() : null,
        value => value == null ? null : DeserializeElement(value))
{
    private static JsonElement DeserializeElement(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();
}

internal sealed class StringListValueComparer()
    : ValueComparer<IReadOnlyList<string>>(
        (left, right) => left == null ? right == null : right != null && left.SequenceEqual(right),
        value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
        value => value == null ? null! : value.ToArray());
