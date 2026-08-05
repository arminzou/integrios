using System.Text.Json;
using Dapper;
using Integrios.Application.Integrations;
using Integrios.Infrastructure.Data;
using Integrios.Application.Common.Pagination;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;

namespace Integrios.Infrastructure.Integrations;

internal sealed class PostgresIntegrationRepository(IDbConnectionFactory connectionFactory)
    : IIntegrationCatalog, IIntegrationManifestStore
{
    private const string SelectColumns =
        "id, key, contract_version, manifest_schema_version, name, direction, supported_auth_schemes::text AS supported_auth_schemes_json, status, description, manifest::text AS manifest_json, created_at, updated_at";

    public async Task<Integration?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM integrations
            WHERE id = @Id
            LIMIT 1
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IntegrationRow? row = await db.QuerySingleOrDefaultAsync<IntegrationRow>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));

        return row?.ToIntegration();
    }

    public async Task<Integration?> GetByVersionAsync(
        string key,
        int contractVersion,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM integrations
            WHERE key = @Key AND contract_version = @ContractVersion
            LIMIT 1
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IntegrationRow? row = await db.QuerySingleOrDefaultAsync<IntegrationRow>(new CommandDefinition(
            sql,
            new { Key = key, ContractVersion = contractVersion },
            cancellationToken: cancellationToken));
        return row?.ToIntegration();
    }

    public async Task<(IReadOnlyList<Integration> Items, string? NextCursor)> ListAsync(
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorTime = default;
        Guid cursorId = default;
        bool hasCursor = afterCursor is not null && PageCursor.TryDecode(afterCursor, out cursorTime, out cursorId);

        var sql = hasCursor
            ? $"""
                SELECT {SelectColumns}
                FROM integrations
                WHERE (created_at, id) > (@CursorTime, @CursorId)
                ORDER BY created_at, id
                LIMIT @Limit
                """
            : $"""
                SELECT {SelectColumns}
                FROM integrations
                ORDER BY created_at, id
                LIMIT @Limit
                """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await db.QueryAsync<IntegrationRow>(
            new CommandDefinition(
                sql,
                new { CursorTime = cursorTime, CursorId = cursorId, Limit = limit + 1 },
                cancellationToken: cancellationToken))).ToList();

        if (rows.Count == 0)
        {
            return ([], null);
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = PageCursor.Encode(rows[^1].CreatedAt, rows[^1].Id);
        }

        return (rows.Select(r => r.ToIntegration()).ToList(), nextCursor);
    }

    public async Task<IntegrationManifestStoreResult> ApplyAsync(
        IntegrationManifest manifest,
        IntegrationManifestApplyAuthority authority,
        CancellationToken cancellationToken)
    {
        const string insertSql = $"""
            INSERT INTO integrations (
                id, key, contract_version, manifest_schema_version, name, direction,
                supported_auth_schemes, status, description, manifest, created_at, updated_at)
            VALUES (
                @Id, @Key, @ContractVersion, @ManifestSchemaVersion, @Name, @Direction,
                @SupportedAuthSchemes::jsonb, 'active', @Description, @Manifest::jsonb, @Now, @Now)
            RETURNING {SelectColumns}
            """;
        const string updateSql = $"""
            UPDATE integrations
            SET name = @Name,
                description = @Description,
                manifest = jsonb_set(manifest, ARRAY['presentation'], @Presentation::jsonb),
                status = @Status,
                updated_at = @Now
            WHERE id = @Id
            RETURNING {SelectColumns}
            """;

        await using var db = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        string identity = $"{manifest.Key}:{manifest.ContractVersion}";
        await db.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Identity, 0))",
            new { Identity = identity },
            transaction,
            cancellationToken: cancellationToken));

        string selectForUpdateSql = $"""
            SELECT {SelectColumns}
            FROM integrations
            WHERE key = @Key AND contract_version = @ContractVersion
            FOR UPDATE
            """;
        IntegrationRow? existingRow = await db.QuerySingleOrDefaultAsync<IntegrationRow>(new CommandDefinition(
            selectForUpdateSql,
            new { manifest.Key, manifest.ContractVersion },
            transaction,
            cancellationToken: cancellationToken));

        JsonElement manifestJson = IntegrationManifestParser.ToJson(manifest);
        Guid id = authority.RequiredIntegrationId ?? Guid.NewGuid();
        object parameters = new
        {
            Id = id,
            manifest.Key,
            manifest.ContractVersion,
            manifest.ManifestSchemaVersion,
            manifest.Presentation.Name,
            Direction = manifest.Direction,
            SupportedAuthSchemes = JsonSerializer.Serialize(
                manifest.DestinationAuthentication.Schemes.Select(scheme => scheme.Scheme)),
            manifest.Presentation.Description,
            Manifest = manifestJson.GetRawText(),
            Status = OperationalStatus.Active.ToString().ToLowerInvariant(),
            Now = DateTimeOffset.UtcNow,
        };

        if (existingRow is null)
        {
            IntegrationRow createdRow = await db.QuerySingleAsync<IntegrationRow>(new CommandDefinition(
                insertSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new IntegrationManifestStoreResult(
                createdRow.ToIntegration(),
                IntegrationManifestApplyOutcome.Created);
        }

        Integration existing = existingRow.ToIntegration();
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
            IntegrationManifestParser.ToPresentationJson(existing.Manifest.Presentation),
            presentationJson);
        bool statusChanged = authority.Mode == IntegrationManifestApplyMode.Bootstrap
            && existing.Status != OperationalStatus.Active;
        if (!presentationChanged && !statusChanged)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IntegrationManifestStoreResult(existing, IntegrationManifestApplyOutcome.Unchanged);
        }

        parameters = new
        {
            Id = existing.Id,
            manifest.Presentation.Name,
            manifest.Presentation.Description,
            Presentation = presentationJson.GetRawText(),
            Status = statusChanged ? "active" : existing.Status.ToString().ToLowerInvariant(),
            Now = DateTimeOffset.UtcNow,
        };
        IntegrationRow updatedRow = await db.QuerySingleAsync<IntegrationRow>(new CommandDefinition(
            updateSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new IntegrationManifestStoreResult(
            updatedRow.ToIntegration(),
            presentationChanged
                ? IntegrationManifestApplyOutcome.PresentationReconciled
                : IntegrationManifestApplyOutcome.Unchanged);
    }

    private sealed record IntegrationRow
    {
        public Guid Id { get; init; }
        public string Key { get; init; } = "";
        public int ContractVersion { get; init; }
        public int ManifestSchemaVersion { get; init; }
        public string Name { get; init; } = "";
        public string Direction { get; init; } = "";
        public string SupportedAuthSchemesJson { get; init; } = "[]";
        public string Status { get; init; } = "";
        public string? Description { get; init; }
        public string ManifestJson { get; init; } = "{}";
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Integration ToIntegration() => new()
        {
            Id = Id,
            Key = Key,
            ContractVersion = ContractVersion,
            ManifestSchemaVersion = ManifestSchemaVersion,
            Name = Name,
            Direction = Enum.Parse<IntegrationDirection>(Direction, ignoreCase: true),
            SupportedAuthSchemes = JsonSerializer.Deserialize<string[]>(SupportedAuthSchemesJson) ?? [],
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Description = Description,
            Manifest = IntegrationManifestParser.DeserializeStored(ManifestJson),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
