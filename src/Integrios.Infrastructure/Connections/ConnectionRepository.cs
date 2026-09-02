using System.Text.Json;
using Integrios.Application.Common.Exceptions;
using Integrios.Application.Authoring.Connections;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;

namespace Integrios.Infrastructure.Connections;

internal sealed class ConnectionRepository(IntegriosDbContext context, IDataProtectionProvider dataProtectionProvider) : IConnectionRepository
{
    public async Task<Connection> CreateAsync(Connection connection, CancellationToken cancellationToken)
    {
        context.Connections.Add(connection);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return connection;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation }
            || ex.InnerException is SqlException { Number: 547 })
        {
            throw new InvalidOperationException("The specified connector does not exist.", ex);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateResourceException(
                $"A connection named '{connection.Name}' already exists for this tenant.",
                ex);
        }
    }

    public async Task<ConnectionUsage> GetUsageAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        bool source = await context.Sources.AsNoTracking().AnyAsync(
            source =>
                source.TenantId == tenantId
                && source.ConnectionId == id
                && source.Status == SourceStatus.Active
                && context.Topics.Any(topic =>
                    topic.TenantId == tenantId
                    && topic.Id == source.TopicId
                    && topic.Status == OperationalStatus.Active),
            cancellationToken);
        bool destination = await context.Subscriptions.AsNoTracking().AnyAsync(
            subscription =>
                subscription.TenantId == tenantId
                && subscription.DestinationConnectionId == id
                && subscription.Status == OperationalStatus.Active,
            cancellationToken);

        return new ConnectionUsage(source, destination);
    }

    public Task<Connection?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.Connections.AsNoTracking().SingleOrDefaultAsync(
            connection => connection.TenantId == tenantId && connection.Id == id,
            cancellationToken);

    public async Task<(IReadOnlyList<Connection> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        OperationalStatus? status,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        string cursorScope = $"connections:{tenantId:N}:{status?.ToString() ?? "all"}";
        bool hasCursor = afterCursor is not null;
        if (hasCursor && !PageCursor.TryDecode(dataProtectionProvider, afterCursor!, cursorScope, out cursorCreatedAt, out cursorId))
            throw new InvalidCursorException();

        IQueryable<Connection> query = context.Connections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId);
        if (status is not null)
            query = query.Where(connection => connection.Status == status);
        if (hasCursor)
        {
            query = query.Where(connection =>
                connection.CreatedAt < cursorCreatedAt
                || (connection.CreatedAt == cursorCreatedAt && connection.Id.CompareTo(cursorId) < 0));
        }

        List<Connection> items = await query
            .OrderByDescending(connection => connection.CreatedAt)
            .ThenByDescending(connection => connection.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            nextCursor = PageCursor.Encode(dataProtectionProvider, cursorScope, items[^1].CreatedAt, items[^1].Id, DateTimeOffset.UtcNow);
        }

        return (items, nextCursor);
    }

    public async Task<Connection?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string name,
        JsonElement config,
        SourceVerification? sourceVerification,
        DestinationAuthentication? destinationAuthentication,
        string? environment,
        string? description,
        CancellationToken cancellationToken)
    {
        try
        {
            int affected = await context.Connections
                .Where(connection => connection.TenantId == tenantId && connection.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(connection => connection.Name, name)
                        .SetProperty(connection => connection.Config, config)
                        .SetProperty(connection => connection.SourceVerification, sourceVerification)
                        .SetProperty(connection => connection.DestinationAuthentication, destinationAuthentication)
                        .SetProperty(connection => connection.Environment, environment)
                        .SetProperty(connection => connection.Description, description)
                        .SetProperty(connection => connection.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);

            return affected == 0 ? null : await GetByIdAsync(tenantId, id, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DuplicateResourceException(
                $"A connection named '{name}' already exists for this tenant.",
                ex);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new DuplicateResourceException(
                $"A connection named '{name}' already exists for this tenant.",
                ex);
        }
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await context.Connections
            .Where(connection =>
                connection.TenantId == tenantId
                && connection.Id == id
                && connection.Status == OperationalStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.Status, OperationalStatus.Disabled)
                    .SetProperty(connection => connection.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken) > 0;
}
