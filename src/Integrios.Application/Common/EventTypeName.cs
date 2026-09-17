namespace Integrios.Application.Common;

// The floor every Event type meets, wherever one is written: the output of a Source mapping or an
// Event API submission, and the Event type a Subscription matches. It is not a naming grammar.
// Providers name their events as they like -- Shopify's `orders/create`, GitLab's `Push Hook` -- and
// Integrios takes those names as they come. What the floor refuses are the values that fail
// silently or damage what shows them: surrounding whitespace looks right and matches nothing,
// control characters break log lines and the ledger, and an unbounded value stored on every Event
// is what a mapping that grabbed the wrong field produces.
public static class EventTypeName
{
    public const int MaxLength = 200;

    // Why the value is not a usable Event type, phrased to follow "Event type", or null when it is.
    public static string? Problem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "must be a non-empty string";
        if (value.Length > MaxLength)
            return $"must be at most {MaxLength} characters";
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return "must not start or end with whitespace";
        if (value.Any(char.IsControl))
            return "must not contain control characters such as tabs or line breaks";
        return null;
    }
}
