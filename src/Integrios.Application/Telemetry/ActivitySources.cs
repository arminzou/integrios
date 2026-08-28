using System.Diagnostics;

namespace Integrios.Application.Telemetry;

public static class ActivitySources
{
    public const string ApplicationName = "integrios.application";

    public static readonly ActivitySource Application = new(ApplicationName);

    // Starts a span attached to the trace carried by a stored W3C traceparent, so async hops
    // continue the originating event's trace. An absent or unparseable traceparent starts a
    // fresh root trace rather than inheriting the current batch-tick span.
    public static Activity? StartLinkedSpan(string name, string? traceparent)
    {
        if (TryParseTraceparent(traceparent, out var parentContext))
            return Application.StartActivity(name, ActivityKind.Internal, parentContext);

        return Application.StartActivity(name, ActivityKind.Internal, default(ActivityContext));
    }

    public static bool TryParseTraceparent(string? traceparent, out ActivityContext context)
    {
        context = default;
        return !string.IsNullOrEmpty(traceparent) && ActivityContext.TryParse(traceparent, null, out context);
    }
}
