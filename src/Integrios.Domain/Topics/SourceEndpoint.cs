namespace Integrios.Domain.Topics;

public sealed record SourceEndpoint
{
    public required Guid Id { get; init; }
    public required string CallbackPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
