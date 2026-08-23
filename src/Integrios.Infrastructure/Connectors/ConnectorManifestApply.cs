using System.Text.Json;
using Integrios.Application.Connectors;
using Integrios.Domain.Common;
using Integrios.Domain.Connectors;

namespace Integrios.Infrastructure.Connectors;

/// <summary>
/// The parts of applying a Connector manifest that carry no dialect: building the new row,
/// rejecting conflicting re-applications, and deciding whether anything actually changed. Each
/// provider store keeps its own lock acquisition and JSON partial-update SQL, which genuinely differ.
/// </summary>
internal static class ConnectorManifestApply
{
    public static Connector NewConnector(
        ConnectorManifest manifest,
        ConnectorManifestApplyAuthority authority,
        DateTimeOffset now) => new()
        {
            Id = authority.RequiredConnectorId ?? Guid.NewGuid(),
            Key = manifest.Key,
            ContractVersion = manifest.ContractVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            Name = manifest.Presentation.Name,
            Direction = Enum.Parse<ConnectorDirection>(manifest.Direction, ignoreCase: true),
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
        Connector existing,
        ConnectorManifest manifest,
        ConnectorManifestApplyAuthority authority)
    {
        if (authority.RequiredConnectorId is Guid requiredId && existing.Id != requiredId)
        {
            throw new ConnectorVersionConflictException(
                $"Built-in Connector '{manifest.Key}' contract version {manifest.ContractVersion} exists with unexpected id '{existing.Id}'.");
        }

        if (!JsonElement.DeepEquals(
                ConnectorManifestParser.ToFunctionalJson(existing.Manifest),
                ConnectorManifestParser.ToFunctionalJson(manifest)))
        {
            throw new ConnectorVersionConflictException(
                $"Connector '{manifest.Key}' contract version {manifest.ContractVersion} already exists with a different functional contract.");
        }
    }

    public static ManifestApplyDelta Diff(
        Connector existing,
        ConnectorManifest manifest,
        ConnectorManifestApplyAuthority authority)
    {
        JsonElement presentationJson = ConnectorManifestParser.ToPresentationJson(manifest.Presentation);
        bool presentationChanged = !JsonElement.DeepEquals(
            ConnectorManifestParser.ToPresentationJson(existing.Manifest.Presentation), presentationJson);
        bool statusChanged = authority.Mode == ConnectorManifestApplyMode.Bootstrap
            && existing.Status != OperationalStatus.Active;
        return new ManifestApplyDelta(
            presentationJson,
            presentationChanged,
            statusChanged,
            statusChanged ? OperationalStatus.Active : existing.Status);
    }

    public static ConnectorManifestApplyOutcome OutcomeFor(bool presentationChanged) =>
        presentationChanged
            ? ConnectorManifestApplyOutcome.PresentationReconciled
            : ConnectorManifestApplyOutcome.Unchanged;
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
