namespace Integrios.Subscriber;

/// The outcome reported for one attempt. Action is the attempt route's final path segment.
internal sealed record Settlement(string Action, int? RetryAfterSeconds, string? Error)
{
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(900);
    private const int MaxErrorLength = 2000;

    public static readonly Settlement Ack = new("ack", null, null);

    public static Settlement Nack(int? retryAfterSeconds, string? error) => new("nack", retryAfterSeconds, Truncate(error));

    public static Settlement Fail(string error) =>
        new("fail", null, Truncate(string.IsNullOrWhiteSpace(error) ? "The handler reported a permanent failure." : error));

    // The Delivery API rejects longer error text, and a rejected settlement would never be recorded.
    private static string? Truncate(string? error) => error?.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
}
