using Integrios.Domain.Topics;

namespace Integrios.Application.Topics;

public sealed record TopicResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    IReadOnlyList<Guid> SourceConnectionIds,
    IReadOnlyList<SourceEndpointResponse> SourceEndpoints,
    string Status,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TopicResponse From(Topic t) => new(
        t.Id,
        t.TenantId,
        t.Name,
        t.SourceConnectionIds,
        t.SourceEndpoints.Select(SourceEndpointResponse.From).ToList(),
        t.Status.ToString().ToLowerInvariant(),
        t.Description,
        t.CreatedAt,
        t.UpdatedAt);
}

public sealed record SourceEndpointResponse(
    Guid Id,
    Guid SourceConnectionId,
    string CallbackPath)
{
    public static SourceEndpointResponse From(SourceEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.ConnectionId,
        endpoint.CallbackPath);
}

public sealed record TopicListResponse(IReadOnlyList<TopicResponse> Items, string? NextCursor);
