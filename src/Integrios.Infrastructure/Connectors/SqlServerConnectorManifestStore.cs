using Dapper;
using Integrios.Application.Connectors;
using Integrios.Domain.Connectors;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Integrios.Infrastructure.Connectors;

internal sealed class SqlServerConnectorManifestStore(IntegriosDbContext context)
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
        string identity = $"connector:{manifest.Key}:{manifest.ContractVersion}";
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

        int affected = await context.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            """
            UPDATE connectors
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
            throw new InvalidOperationException("The Connector changed while its manifest was being applied.");

        Connector updated = await context.Connectors.AsNoTracking().SingleAsync(
            connector => connector.Id == existing.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConnectorManifestStoreResult(
            updated, ConnectorManifestApply.OutcomeFor(delta.PresentationChanged));
    }
}
