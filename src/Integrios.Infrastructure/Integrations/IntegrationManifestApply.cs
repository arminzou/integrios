using System.Text.Json;
using Integrios.Application.Integrations;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;

namespace Integrios.Infrastructure.Integrations;

/// <summary>
/// The parts of applying an Integration manifest that carry no dialect: building the new row,
/// rejecting conflicting re-applications, and deciding whether anything actually changed. Each
/// provider store keeps its own lock acquisition and JSON partial-update SQL, which genuinely differ.
/// </summary>
internal static class IntegrationManifestApply
{
    public static Integration NewIntegration(
        IntegrationManifest manifest,
        IntegrationManifestApplyAuthority authority,
        DateTimeOffset now) => new()
        {
            Id = authority.RequiredIntegrationId ?? Guid.NewGuid(),
            Key = manifest.Key,
            ContractVersion = manifest.ContractVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            Name = manifest.Presentation.Name,
            Direction = Enum.Parse<IntegrationDirection>(manifest.Direction, ignoreCase: true),
            SupportedAuthSchemes = manifest.DestinationAuthentication.Schemes
                .Select(scheme => scheme.Scheme)
                .ToArray(),
            Status = OperationalStatus.Active,
            Description = manifest.Presentation.Description,
            Manifest = manifest,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public static void EnsureApplicable(
        Integration existing,
        IntegrationManifest manifest,
        IntegrationManifestApplyAuthority authority)
    {
        if (authority.RequiredIntegrationId is Guid requiredId && existing.Id != requiredId)
        {
            throw new IntegrationVersionConflictException(
                $"Built-in Integration '{manifest.Key}' contract version {manifest.ContractVersion} exists with unexpected id '{existing.Id}'.");
        }

        if (!JsonElement.DeepEquals(
                IntegrationManifestParser.ToFunctionalJson(existing.Manifest),
                IntegrationManifestParser.ToFunctionalJson(manifest)))
        {
            throw new IntegrationVersionConflictException(
                $"Integration '{manifest.Key}' contract version {manifest.ContractVersion} already exists with a different functional contract.");
        }
    }

    public static ManifestApplyDelta Diff(
        Integration existing,
        IntegrationManifest manifest,
        IntegrationManifestApplyAuthority authority)
    {
        JsonElement presentationJson = IntegrationManifestParser.ToPresentationJson(manifest.Presentation);
        bool presentationChanged = !JsonElement.DeepEquals(
            IntegrationManifestParser.ToPresentationJson(existing.Manifest.Presentation), presentationJson);
        bool statusChanged = authority.Mode == IntegrationManifestApplyMode.Bootstrap
            && existing.Status != OperationalStatus.Active;
        return new ManifestApplyDelta(
            presentationJson,
            presentationChanged,
            statusChanged,
            statusChanged ? OperationalStatus.Active : existing.Status);
    }

    public static IntegrationManifestApplyOutcome OutcomeFor(bool presentationChanged) =>
        presentationChanged
            ? IntegrationManifestApplyOutcome.PresentationReconciled
            : IntegrationManifestApplyOutcome.Unchanged;
}

internal readonly record struct ManifestApplyDelta(
    JsonElement PresentationJson,
    bool PresentationChanged,
    bool StatusChanged,
    OperationalStatus Status)
{
    public bool NothingToWrite => !PresentationChanged && !StatusChanged;

    public string StatusValue => Status.ToString().ToLowerInvariant();
}
