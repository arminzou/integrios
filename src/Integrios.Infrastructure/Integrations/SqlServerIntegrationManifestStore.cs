using Dapper;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Integrios.Infrastructure.Integrations;

internal sealed class SqlServerIntegrationManifestStore(IntegriosDbContext context)
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
        string identity = $"integration:{manifest.Key}:{manifest.ContractVersion}";
        // Transaction-scoped owner: released by the commit below, no explicit unlock.
        int lockResult = await context.Database.GetDbConnection().ExecuteScalarAsync<int>(new CommandDefinition(
            """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource=@Identity, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=60000;
            SELECT @result;
            """,
            new { Identity = identity },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));
        if (lockResult < 0)
            throw new InvalidOperationException($"SQL Server manifest lock failed with code {lockResult}.");

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

        int affected = await context.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            """
            UPDATE integrations
            SET name = @Name,
                description = @Description,
                manifest = JSON_MODIFY(manifest, '$.presentation', JSON_QUERY(@Presentation)),
                status = @Status,
                updated_at = @UpdatedAt
            WHERE id = @Id
            """,
            new
            {
                manifest.Presentation.Name,
                manifest.Presentation.Description,
                Presentation = delta.PresentationJson.GetRawText(),
                Status = delta.StatusValue,
                UpdatedAt = DateTimeOffset.UtcNow,
                existing.Id,
            },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));
        if (affected != 1)
            throw new InvalidOperationException("The Integration changed while its manifest was being applied.");

        Integration updated = await context.Integrations.AsNoTracking().SingleAsync(
            integration => integration.Id == existing.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntegrationManifestStoreResult(
            updated, IntegrationManifestApply.OutcomeFor(delta.PresentationChanged));
    }
}
