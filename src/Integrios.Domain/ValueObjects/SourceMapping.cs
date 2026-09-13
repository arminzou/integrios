namespace Integrios.Domain.ValueObjects;

public sealed record SourceMapping
{
    public required string Engine { get; init; }
    public required string Version { get; init; }
    public required string Expression { get; init; }
}
