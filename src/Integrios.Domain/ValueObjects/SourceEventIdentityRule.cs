namespace Integrios.Domain.ValueObjects;

public sealed record SourceEventIdentityRule
{
    public required string Kind { get; init; }
    public required string Value { get; init; }

    // Whether a request carrying no value at this identity is accepted rather than rejected. Named
    // for the permission it grants, as the Connector capability flags are, and false by default so a
    // rule stored before this field existed — and a caller that omits it — keeps rejecting. A
    // provider that omits its delivery header on a replay is the case this exists for.
    public bool AllowMissing { get; init; }
}
