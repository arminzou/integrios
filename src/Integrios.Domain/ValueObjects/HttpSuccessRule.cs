namespace Integrios.Domain.ValueObjects;

public sealed record HttpSuccessRule
{
    public const int DefaultMaxBodyBytes = 65_536;

    public required string Evaluator { get; init; }
    public string? Field { get; init; }
    public bool? Expected { get; init; }
    public string? DiagnosticField { get; init; }
    public int? MaxBodyBytes { get; init; }
}
