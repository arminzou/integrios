using System.Text.Json;

namespace Integrios.Tests.Shared;

// The one serializer the host-facing test projects read and write bodies with. Both hosts set
// snake_case by policy, so a test that builds its own options is testing a casing the product
// does not use.
public static class HostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
