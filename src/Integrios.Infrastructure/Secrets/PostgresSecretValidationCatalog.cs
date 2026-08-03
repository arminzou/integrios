using System.Text.Json;
using Dapper;
using Integrios.Application.Secrets;
using Integrios.Infrastructure.Data;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Domain.Tenants;

namespace Integrios.Infrastructure.Secrets;

internal sealed class PostgresSecretValidationCatalog(IDbConnectionFactory connectionFactory)
    : ISecretValidationCatalog
{
    private const string TenantColumns =
        "id, slug, name, status, environment, description, created_at, updated_at";

    private const string ConnectionColumns = """
        id, tenant_id, integration_id, name,
        config::text AS ConfigJson,
        source_verification::text AS SourceVerificationJson,
        destination_authentication::text AS DestinationAuthenticationJson,
        status, environment, description, created_at, updated_at
        """;

    public async Task<Tenant?> FindTenantBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        TenantRow? row = await connection.QuerySingleOrDefaultAsync<TenantRow>(
            new CommandDefinition(
                $"SELECT {TenantColumns} FROM tenants WHERE slug = @Slug",
                new { Slug = slug },
                cancellationToken: cancellationToken));

        return row?.ToTenant();
    }

    public async Task<IReadOnlyList<Tenant>> ListActiveTenantsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IEnumerable<TenantRow> rows = await connection.QueryAsync<TenantRow>(
            new CommandDefinition(
                $"""
                SELECT {TenantColumns}
                FROM tenants
                WHERE status = 'active'
                ORDER BY created_at, id
                """,
                cancellationToken: cancellationToken));

        return rows.Select(row => row.ToTenant()).ToList();
    }

    public async Task<Connection?> FindConnectionAsync(
        Guid tenantId,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        ConnectionRow? row = await connection.QuerySingleOrDefaultAsync<ConnectionRow>(
            new CommandDefinition(
                $"""
                SELECT {ConnectionColumns}
                FROM connections
                WHERE tenant_id = @TenantId AND id = @ConnectionId
                """,
                new { TenantId = tenantId, ConnectionId = connectionId },
                cancellationToken: cancellationToken));

        return row?.ToConnection();
    }

    public async Task<IReadOnlyList<Connection>> ListActiveConnectionsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        IEnumerable<ConnectionRow> rows = await connection.QueryAsync<ConnectionRow>(
            new CommandDefinition(
                $"""
                SELECT {ConnectionColumns}
                FROM connections
                WHERE tenant_id = @TenantId AND status = 'active'
                ORDER BY created_at, id
                """,
                new { TenantId = tenantId },
                cancellationToken: cancellationToken));

        return rows.Select(row => row.ToConnection()).ToList();
    }

    private sealed record TenantRow
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = "";
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Environment { get; init; }
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Tenant ToTenant() => new()
        {
            Id = Id,
            Slug = Slug,
            Name = Name,
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Environment = Environment,
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    private sealed record ConnectionRow
    {
        public Guid Id { get; init; }
        public Guid TenantId { get; init; }
        public Guid IntegrationId { get; init; }
        public string Name { get; init; } = "";
        public string ConfigJson { get; init; } = "{}";
        public string? SourceVerificationJson { get; init; }
        public string? DestinationAuthenticationJson { get; init; }
        public string Status { get; init; } = "";
        public string? Environment { get; init; }
        public string? Description { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Connection ToConnection() => new()
        {
            Id = Id,
            TenantId = TenantId,
            IntegrationId = IntegrationId,
            Name = Name,
            Config = JsonSerializer.Deserialize<JsonElement>(ConfigJson),
            SourceVerification = SourceVerificationJson is null
                ? null
                : JsonSerializer.Deserialize<ConnectionSchemeSelection>(SourceVerificationJson),
            DestinationAuthentication = DestinationAuthenticationJson is null
                ? null
                : JsonSerializer.Deserialize<ConnectionSchemeSelection>(DestinationAuthenticationJson),
            Status = Enum.Parse<OperationalStatus>(Status, ignoreCase: true),
            Environment = Environment,
            Description = Description,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
