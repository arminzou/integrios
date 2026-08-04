using Integrios.Domain.Topics;

namespace Integrios.Application.Topics;

public sealed record SourceEndpointDto(
    Guid Id,
    string CallbackPath,
    DateTimeOffset CreatedAt)
{
    public static SourceEndpointDto From(SourceEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.CallbackPath,
        endpoint.CreatedAt);
}
