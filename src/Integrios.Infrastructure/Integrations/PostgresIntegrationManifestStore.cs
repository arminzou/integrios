using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Integrations;

internal sealed class PostgresIntegrationManifestStore(IntegriosDbContext context)
    : IIntegrationManifestStore
{
    public Task<Integration?> GetByVersionAsync(
        string key,
        int contractVersion,
        CancellationToken cancellationToken) =>
        context.Integrations.AsNoTracking().SingleOrDefaultAsync(
            integration => integration.Key == key && integration.ContractVersion == contractVersion,
            cancellationToken);

    public async Task<IntegrationManifestStoreResult> ApplyAsync(
        IntegrationManifest manifest,
        IntegrationManifestApplyAuthority authority,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        string identity = $"{manifest.Key}:{manifest.ContractVersion}";
        // Transaction-scoped: released by the commit below, no explicit unlock.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))",
            cancellationToken);

        Integration? existing = await GetByVersionAsync(manifest.Key, manifest.ContractVersion, cancellationToken);
        if (existing is null)
        {
            context.Integrations.Add(IntegrationManifestApply.NewIntegration(
                manifest, authority, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync(cancellationToken);
            Integration persisted = await GetByVersionAsync(
                manifest.Key, manifest.ContractVersion, cancellationToken)
                ?? throw new InvalidOperationException("The created Integration could not be reloaded.");
            await transaction.CommitAsync(cancellationToken);
            return new IntegrationManifestStoreResult(persisted, IntegrationManifestApplyOutcome.Created);
        }

        IntegrationManifestApply.EnsureApplicable(existing, manifest, authority);
        ManifestApplyDelta delta = IntegrationManifestApply.Diff(existing, manifest, authority);
        if (delta.NothingToWrite)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IntegrationManifestStoreResult(existing, IntegrationManifestApplyOutcome.Unchanged);
        }

        string presentation = delta.PresentationJson.GetRawText();
        int affected = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE integrations
            SET name = {manifest.Presentation.Name},
                description = {manifest.Presentation.Description},
                manifest = jsonb_set(manifest, ARRAY['presentation'], CAST({presentation} AS jsonb)),
                status = {delta.StatusValue},
                updated_at = {DateTimeOffset.UtcNow}
            WHERE id = {existing.Id}
            """,
            cancellationToken);
        if (affected != 1)
            throw new InvalidOperationException("The Integration changed while its manifest was being applied.");

        Integration updated = await context.Integrations.AsNoTracking().SingleAsync(
            integration => integration.Id == existing.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntegrationManifestStoreResult(
            updated, IntegrationManifestApply.OutcomeFor(delta.PresentationChanged));
    }
}
