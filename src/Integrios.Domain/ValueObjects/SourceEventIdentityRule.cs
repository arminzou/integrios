namespace Integrios.Domain.ValueObjects;

public sealed record SourceEventIdentityRule
{
    public required string Kind { get; init; }
    public required string Value { get; init; }
}
