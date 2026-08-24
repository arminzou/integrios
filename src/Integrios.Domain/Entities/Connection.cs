using System.Text.Json;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Domain.Entities;

public sealed record Connection
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid ConnectorId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Config { get; init; }
    public SourceVerification? SourceVerification { get; init; }
    public DestinationAuthentication? DestinationAuthentication { get; init; }
    public required OperationalStatus Status { get; init; }
    public string? Environment { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
