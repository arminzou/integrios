using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Connectors;

internal sealed class PostgresConnectorManifestStore(IntegriosDbContext context)
    : IConnectorManifestStore
{
    public Task<Connector?> GetByVersionAsync(
        string key,
        int contractVersion,
        CancellationToken cancellationToken) =>
        context.Connectors.AsNoTracking().SingleOrDefaultAsync(
            connector => connector.Key == key && connector.ContractVersion == contractVersion,
            cancellationToken);

    public async Task<ConnectorManifestStoreResult> ApplyAsync(
        ConnectorManifest manifest,
        ConnectorManifestApplyAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        string identity = $"{manifest.Key}:{manifest.ContractVersion}";
        // Transaction-scoped: released by the commit below, no explicit unlock.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))",
            cancellationToken);

        Connector? existing = await GetByVersionAsync(manifest.Key, manifest.ContractVersion, cancellationToken);
        if (existing is null)
        {
            context.Connectors.Add(ConnectorManifestApply.NewConnector(
                manifest, authority, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync(cancellationToken);
            Connector persisted = await GetByVersionAsync(
                manifest.Key, manifest.ContractVersion, cancellationToken)
                ?? throw new InvalidOperationException("The created Connector could not be reloaded.");
            await transaction.CommitAsync(cancellationToken);
            return new ConnectorManifestStoreResult(persisted, ConnectorManifestApplyOutcome.Created);
        }

        ConnectorManifestApply.EnsureApplicable(existing, manifest, authority);
        ManifestApplyDelta delta = ConnectorManifestApply.Diff(existing, manifest, authority);
        if (delta.NothingToWrite)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ConnectorManifestStoreResult(existing, ConnectorManifestApplyOutcome.Unchanged);
        }

        string presentation = delta.PresentationJson.GetRawText();
        int affected = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE connectors
            SET name = {manifest.Presentation.Name},
                description = {manifest.Presentation.Description},
                manifest = jsonb_set(manifest, ARRAY['presentation'], CAST({presentation} AS jsonb)),
                status = {delta.StatusValue},
                updated_at = {DateTimeOffset.UtcNow}
            WHERE id = {existing.Id}
            """,
            cancellationToken);
        if (affected != 1)
            throw new InvalidOperationException("The Connector changed while its manifest was being applied.");

        Connector updated = await context.Connectors.AsNoTracking().SingleAsync(
            connector => connector.Id == existing.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConnectorManifestStoreResult(
            updated, ConnectorManifestApply.OutcomeFor(delta.PresentationChanged));
    }
}
