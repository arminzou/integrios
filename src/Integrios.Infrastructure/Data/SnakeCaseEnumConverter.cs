using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Integrios.Infrastructure.Data;

internal sealed class SnakeCaseEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(ToProviderExpression, FromProviderExpression)
    where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<TEnum, string> ToProvider =
        Enum.GetValues<TEnum>().ToDictionary(
            value => value,
            value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()));

    private static readonly IReadOnlyDictionary<string, TEnum> FromProvider =
        ToProvider.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static readonly Expression<Func<TEnum, string>> ToProviderExpression =
        value => MapToProvider(value);

    private static readonly Expression<Func<string, TEnum>> FromProviderExpression =
        value => MapFromProvider(value);

    private static string MapToProvider(TEnum value) =>
        ToProvider.TryGetValue(value, out string? stored)
            ? stored
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Unmapped {typeof(TEnum).Name} value.");

    private static TEnum MapFromProvider(string value) =>
        FromProvider.TryGetValue(value, out TEnum parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Unknown {typeof(TEnum).Name} value.");
}
