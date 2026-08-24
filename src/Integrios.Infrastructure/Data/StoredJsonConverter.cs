using System.Linq.Expressions;
using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Integrios.Infrastructure.Data;

internal sealed class StoredJsonConverter<T>()
    : ValueConverter<T, string>(ToProviderExpression, FromProviderExpression)
{
    private static readonly Expression<Func<T, string>> ToProviderExpression =
        value => JsonSerializer.Serialize(value, StoredJson.Options);

    private static readonly Expression<Func<string, T>> FromProviderExpression =
        value => Deserialize(value);

    private static T Deserialize(string value) =>
        JsonSerializer.Deserialize<T>(value, StoredJson.Options)
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
