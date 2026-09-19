namespace Integrios.Admin;

/// A deployment's link from a trace id to its tracing product, such as
/// https://tracing.example/trace/{trace_id}. Optional: without one, Event diagnostics offer the
/// trace id to copy and no link. Validated once at startup so a mistyped template fails the
/// deployment rather than handing Operators broken links.
internal sealed class TraceUrlTemplate
{
    internal const string ConfigurationKey = "Integrios:Admin:TraceUrlTemplate";
    private const string Placeholder = "{trace_id}";

    // A representative W3C trace id: the template must stay a valid URI once one is substituted.
    private const string SampleTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    private readonly string? template;

    private TraceUrlTemplate(string? template)
    {
        this.template = template;
    }

    internal static TraceUrlTemplate Parse(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return new TraceUrlTemplate(null);

        int first = configuredValue.IndexOf(Placeholder, StringComparison.Ordinal);
        if (first < 0 || configuredValue.IndexOf(Placeholder, first + Placeholder.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{ConfigurationKey} must contain {Placeholder} exactly once.");

        // Query strings and fragments stay allowed: tracing products commonly carry the id in either.
        if (!Uri.TryCreate(configuredValue.Replace(Placeholder, SampleTraceId, StringComparison.Ordinal), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be an absolute HTTP(S) URI with no user info once {Placeholder} is replaced.");
        }

        return new TraceUrlTemplate(configuredValue);
    }

    public string? Resolve(string? traceId) =>
        template is null || string.IsNullOrEmpty(traceId)
            ? null
            : template.Replace(Placeholder, Uri.EscapeDataString(traceId), StringComparison.Ordinal);
}
