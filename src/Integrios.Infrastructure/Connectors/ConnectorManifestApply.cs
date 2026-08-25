using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

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
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            Key = manifest.Key,
            ContractVersion = manifest.ContractVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            Name = manifest.Presentation.Name,
            Direction = Enum.Parse<ConnectorDirection>(manifest.Direction, ignoreCase: true),
            Status = OperationalStatus.Active,
            Description = manifest.Presentation.Description,
            Manifest = manifest,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public static void EnsureApplicable(
        Connector existing,
        ConnectorManifest manifest)
    {
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
        ConnectorManifest manifest)
    {
        JsonElement presentationJson = ConnectorManifestParser.ToPresentationJson(manifest.Presentation);
        bool presentationChanged = !JsonElement.DeepEquals(
            ConnectorManifestParser.ToPresentationJson(existing.Manifest.Presentation), presentationJson);
        return new ManifestApplyDelta(
            presentationJson,
            presentationChanged);
    }

    public static ConnectorManifestApplyOutcome OutcomeFor(bool presentationChanged) =>
        presentationChanged
            ? ConnectorManifestApplyOutcome.PresentationReconciled
            : ConnectorManifestApplyOutcome.Unchanged;
}

internal readonly record struct ManifestApplyDelta(
    JsonElement PresentationJson,
    bool PresentationChanged)
{
    public bool NothingToWrite => !PresentationChanged;
}
