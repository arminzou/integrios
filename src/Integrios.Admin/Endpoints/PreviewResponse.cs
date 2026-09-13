using System.Text.Json;

namespace Integrios.Admin.Endpoints;

/// What a stateless dry-run answers with. Named rather than anonymous so the preview declares a
/// response schema and the generated browser client reads its output as a typed value.
internal sealed record PreviewResponse(JsonElement Output);
