using System.Net.Http.Headers;

namespace Integrios.Infrastructure.Delivery;

internal static class RetryAfterParser
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(15);

    public static TimeSpan? Parse(HttpResponseMessage response, DateTimeOffset now)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        TimeSpan? delta = header?.Delta ?? (header?.Date is { } date ? date - now : null);
        if (delta is null || delta <= TimeSpan.Zero)
            return null;

        return delta.Value > MaxRetryAfter ? MaxRetryAfter : delta.Value;
    }
}
