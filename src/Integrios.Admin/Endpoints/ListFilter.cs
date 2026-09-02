using Integrios.Application.Common.Exceptions;

namespace Integrios.Admin.Endpoints;

internal static class ListFilter
{
    /// Parses an optional lowercase enum-name query value; numeric and unknown values are rejected with 400.
    public static TEnum? ParseEnum<TEnum>(string? value, string message) where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (!int.TryParse(value, out _) && Enum.TryParse(value, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidListFilterException(message);
    }
}
