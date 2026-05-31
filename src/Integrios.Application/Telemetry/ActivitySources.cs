using System.Diagnostics;

namespace Integrios.Application.Telemetry;

public static class ActivitySources
{
    public const string ApplicationName = "integrios.application";

    public static readonly ActivitySource Application = new(ApplicationName);
}
