using System.Text.Json;
using Dapper;
using Integrios.Application.Integrations;
using Integrios.Domain.Common;
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
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var created = new Integration
            {
                Id = authority.RequiredIntegrationId ?? Guid.NewGuid(),
                Key = manifest.Key,
                ContractVersion = manifest.ContractVersion,
                ManifestSchemaVersion = manifest.ManifestSchemaVersion,
                Name = manifest.Presentation.Name,
                Direction = Enum.Parse<IntegrationDirection>(manifest.Direction, ignoreCase: true),
                SupportedAuthSchemes = manifest.DestinationAuthentication.Schemes.Select(scheme => scheme.Scheme).ToArray(),
                Status = OperationalStatus.Active,
                Description = manifest.Presentation.Description,
                Manifest = manifest,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.Integrations.Add(created);
            await context.SaveChangesAsync(cancellationToken);
            Integration persisted = await GetByVersionAsync(manifest.Key, manifest.ContractVersion, cancellationToken)
                ?? throw new InvalidOperationException("The created Integration could not be reloaded.");
            await transaction.CommitAsync(cancellationToken);
            return new IntegrationManifestStoreResult(persisted, IntegrationManifestApplyOutcome.Created);
        }

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

        JsonElement presentationJson = IntegrationManifestParser.ToPresentationJson(manifest.Presentation);
        bool presentationChanged = !JsonElement.DeepEquals(
            IntegrationManifestParser.ToPresentationJson(existing.Manifest.Presentation), presentationJson);
        bool statusChanged = authority.Mode == IntegrationManifestApplyMode.Bootstrap
            && existing.Status != OperationalStatus.Active;
        if (!presentationChanged && !statusChanged)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IntegrationManifestStoreResult(existing, IntegrationManifestApplyOutcome.Unchanged);
        }

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
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
                Presentation = presentationJson.GetRawText(),
                Status = (statusChanged ? OperationalStatus.Active : existing.Status).ToString().ToLowerInvariant(),
                UpdatedAt = updatedAt,
                existing.Id,
            },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));
        if (affected != 1)
            throw new InvalidOperationException("The Integration changed while its manifest was being applied.");

        Integration updated = await context.Integrations.AsNoTracking().SingleAsync(
            integration => integration.Id == existing.Id,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntegrationManifestStoreResult(
            updated,
            presentationChanged
                ? IntegrationManifestApplyOutcome.PresentationReconciled
                : IntegrationManifestApplyOutcome.Unchanged);
    }
}
