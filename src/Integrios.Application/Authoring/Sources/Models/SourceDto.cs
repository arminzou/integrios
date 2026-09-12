using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Sources;

public sealed record SourceDto(
    Guid Id,
    Guid TenantId,
    Guid ConnectorId,
    Guid TopicId,
    string Name,
    string Type,
    JsonElement Configuration,
    SourceVerificationDto? Verification,
    JsonElement? InputRequirements,
    SourceMapping? Mapping,
    SourceEventIdentityRule? EventIdentityRule,
    string Revision,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt)
{
    public static SourceDto From(Source source) => new(
        source.Id,
        source.TenantId,
        source.ConnectorId,
        source.TopicId,
        source.Name,
        source.Type == SourceType.EventApi ? "event_api" : source.Type.ToString().ToLowerInvariant(),
        source.Configuration,
        SourceVerificationDto.From(source.Verification),
        source.InputRequirements,
        source.Mapping,
        source.EventIdentityRule,
        source.Revision,
        source.Status.ToString().ToLowerInvariant(),
        source.CreatedAt,
        source.UpdatedAt,
        source.RevokedAt);
}
