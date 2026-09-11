using System.Text.Json;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Common.Exceptions;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Common.Pagination;
using Integrios.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Integrios.Infrastructure.Destinations;

internal sealed class DestinationRepository(IntegriosDbContext context, IDataProtectionProvider dataProtectionProvider)
    : IDestinationRepository
{
    public async Task<Destination> CreateAsync(Destination destination, CancellationToken cancellationToken)
    {
        context.Destinations.Add(destination);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return destination;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation }
            || exception.InnerException is SqlException { Number: 547 })
        {
            throw new InvalidOperationException("The specified Connector does not exist.", exception);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            || exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw Duplicate(destination.Name, exception);
        }
    }

    public Task<Destination?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.Destinations.AsNoTracking().SingleOrDefaultAsync(
            destination => destination.TenantId == tenantId && destination.Id == id,
            cancellationToken);

    public Task<bool> HasActiveSubscriptionsAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        context.Subscriptions.AsNoTracking().AnyAsync(
            subscription => subscription.TenantId == tenantId
                && subscription.DestinationId == id
                && subscription.Status == OperationalStatus.Active,
            cancellationToken);

    public async Task<(IReadOnlyList<DestinationListRow> Items, string? NextCursor)> ListByTenantAsync(
        Guid tenantId,
        DestinationListFilter filter,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        string scope = "destinations:" + JsonSerializer.Serialize(new
        {
            tenantId,
            filter.Status,
            filter.Environment,
            filter.ConnectorKey,
            filter.NameContains,
        });
        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        if (afterCursor is not null && !PageCursor.TryDecode(
                dataProtectionProvider, afterCursor, scope, out cursorCreatedAt, out cursorId))
        {
            throw new InvalidCursorException();
        }

        IQueryable<Destination> destinations = context.Destinations.AsNoTracking()
            .Where(destination => destination.TenantId == tenantId);
        if (filter.Status is not null)
            destinations = destinations.Where(destination => destination.Status == filter.Status);
        if (filter.Environment is not null)
        {
            string environment = filter.Environment.ToLowerInvariant();
            destinations = destinations.Where(destination => destination.Environment!.ToLower() == environment);
        }
        if (filter.ConnectorKey is not null)
            destinations = destinations.Where(destination => context.Connectors.Any(
                connector => connector.Id == destination.ConnectorId && connector.Key == filter.ConnectorKey));
        if (filter.NameContains is not null)
        {
            string name = filter.NameContains.ToLowerInvariant();
            destinations = destinations.Where(destination => destination.Name.ToLower().Contains(name));
        }
        if (afterCursor is not null)
            destinations = destinations.Where(destination => destination.CreatedAt < cursorCreatedAt
                || (destination.CreatedAt == cursorCreatedAt && destination.Id.CompareTo(cursorId) < 0));

        var page = await destinations
            .OrderByDescending(destination => destination.CreatedAt)
            .ThenByDescending(destination => destination.Id)
            .Take(limit + 1)
            .Select(destination => new
            {
                Destination = destination,
                ConnectorKey = context.Connectors.Where(connector => connector.Id == destination.ConnectorId)
                    .Select(connector => connector.Key).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        List<DestinationListRow> items = page
            .Select(row => new DestinationListRow(row.Destination, row.ConnectorKey ?? ""))
            .ToList();
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            Destination last = items[^1].Destination;
            next = PageCursor.Encode(dataProtectionProvider, scope, last.CreatedAt, last.Id, DateTimeOffset.UtcNow);
        }

        return (items, next);
    }

    public async Task<Destination?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string name,
        JsonElement configuration,
        DestinationAuthentication? authentication,
        string? environment,
        string? description,
        CancellationToken cancellationToken)
    {
        try
        {
            int changed = await context.Destinations
                .Where(destination => destination.TenantId == tenantId && destination.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(destination => destination.Name, name)
                    .SetProperty(destination => destination.Configuration, configuration)
                    .SetProperty(destination => destination.Authentication, authentication)
                    .SetProperty(destination => destination.Environment, environment)
                    .SetProperty(destination => destination.Description, description)
                    .SetProperty(destination => destination.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
            return changed == 0 ? null : await GetByIdAsync(tenantId, id, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw Duplicate(name, exception);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw Duplicate(name, exception);
        }
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await context.Destinations.Where(destination => destination.TenantId == tenantId
                && destination.Id == id
                && destination.Status == OperationalStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(destination => destination.Status, OperationalStatus.Disabled)
                .SetProperty(destination => destination.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) > 0;

    private static DuplicateResourceException Duplicate(string name, Exception exception) =>
        new($"A Destination named '{name}' already exists for this Tenant.", exception);
}
