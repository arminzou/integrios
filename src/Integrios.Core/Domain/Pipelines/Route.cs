using System.Text.Json;
using Integrios.Core.Domain.Common;

namespace Integrios.Core.Domain.Pipelines;

public sealed record Route
{
    public required Guid Id { get; init; }
    public required Guid PipelineId { get; init; }
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required JsonElement MatchRules { get; init; }
    public required Guid DestinationConnectionId { get; init; }
    public JsonElement? TransformConfig { get; init; }
    public JsonElement? DeliveryPolicy { get; init; }
    public required OperationalStatus Status { get; init; }
    public required int OrderIndex { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
