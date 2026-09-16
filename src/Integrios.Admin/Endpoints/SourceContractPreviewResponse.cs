using System.Text.Json;

namespace Integrios.Admin.Endpoints;

/// What a Source-contract dry-run answers with. Separate from <see cref="PreviewResponse"/> because
/// a Source accepts an Event, not just a mapping output: the identity rule runs before the mapping
/// and decides whether the input is admitted at all, so the resolved identity belongs beside the
/// output rather than only in the caller's head.
///
/// <c>SourceEventId</c> is null where nothing resolved it — a rule reading a broker message id,
/// which a sample cannot carry, or an absent value the rule permits.
internal sealed record SourceContractPreviewResponse(JsonElement Output, string? SourceEventId);
