using System.Text.Json;
using Integrios.Core.Domain.Common;

namespace Integrios.Core.Domain.Integrations;

public sealed record Connection
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid IntegrationId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Config { get; init; }
    public required JsonElement SecretReferences { get; init; }
    public required OperationalStatus Status { get; init; }
    public string? Environment { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Description { get; init; }
}
