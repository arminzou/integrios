using System.Text.Json;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using Integrios.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Integrios.Infrastructure.Subscriptions;

internal sealed class SubscriptionRepository(IntegriosDbContext context) : ISubscriptionRepository
{
    public async Task<Subscription?> CreateAsync(
        Guid tenantId,
        Guid topicId,
        string name,
        JsonElement matchRules,
        Guid destinationId,
        JsonElement? transformConfig,
        HttpDeliveryConfiguration httpDelivery,
        HttpSuccessRule? httpSuccess,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken)
    {
        bool validOwnership = await context.Topics.AsNoTracking().AnyAsync(
            topic =>
                topic.Id == topicId
                && topic.TenantId == tenantId
                && context.Destinations.Any(destination =>
                    destination.Id == destinationId
                    && destination.TenantId == topic.TenantId),
            cancellationToken);
        if (!validOwnership)
        {
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TopicId = topicId,
            Name = name,
            MatchRules = matchRules,
            DestinationId = destinationId,
            MappingConfig = NormalizeNullableJson(transformConfig),
            HttpDelivery = httpDelivery,
            HttpSuccess = httpSuccess,
            Status = OperationalStatus.Active,
            OrderIndex = orderIndex,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Subscriptions.Add(subscription);
        await context.SaveChangesAsync(cancellationToken);
        return subscription;
    }

    public Task<Subscription?> GetByIdAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        CancellationToken cancellationToken) =>
        context.Subscriptions.AsNoTracking().SingleOrDefaultAsync(
            subscription =>
                subscription.TenantId == tenantId
                && subscription.TopicId == topicId
                && subscription.Id == id,
            cancellationToken);

    public async Task<Subscription?> UpdateAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        string name,
        JsonElement matchRules,
        Guid destinationId,
        JsonElement? transformConfig,
        HttpDeliveryConfiguration httpDelivery,
        HttpSuccessRule? httpSuccess,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken)
    {
        bool destinationBelongsToTenant = await context.Destinations.AsNoTracking().AnyAsync(
            destination => destination.TenantId == tenantId && destination.Id == destinationId,
            cancellationToken);
        if (!destinationBelongsToTenant)
        {
            return null;
        }

        int affected = await context.Subscriptions
            .Where(subscription =>
                subscription.TenantId == tenantId
                && subscription.TopicId == topicId
                && subscription.Id == id
                && subscription.Status != OperationalStatus.Disabled)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(subscription => subscription.Name, name)
                    .SetProperty(subscription => subscription.MatchRules, matchRules)
                    .SetProperty(subscription => subscription.DestinationId, destinationId)
                    .SetProperty(subscription => subscription.MappingConfig, NormalizeNullableJson(transformConfig))
                    .SetProperty(subscription => subscription.HttpDelivery, httpDelivery)
                    .SetProperty(subscription => subscription.HttpSuccess, httpSuccess)
                    .SetProperty(subscription => subscription.OrderIndex, orderIndex)
                    .SetProperty(subscription => subscription.Description, description)
                    .SetProperty(subscription => subscription.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);

        return affected == 0 ? null : await GetByIdAsync(tenantId, topicId, id, cancellationToken);
    }

    public async Task<bool> DeactivateAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        CancellationToken cancellationToken) =>
        await context.Subscriptions
            .Where(subscription =>
                subscription.TenantId == tenantId
                && subscription.TopicId == topicId
                && subscription.Id == id
                && subscription.Status != OperationalStatus.Disabled)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(subscription => subscription.Status, OperationalStatus.Disabled)
                    .SetProperty(subscription => subscription.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken) > 0;

    public async Task<IReadOnlyList<HttpDeliveryConfiguration>> ListActiveHttpDeliveriesAsync(
        Guid tenantId,
        Guid destinationId,
        CancellationToken cancellationToken) =>
        await context.Subscriptions.AsNoTracking()
            .Where(subscription =>
                subscription.TenantId == tenantId
                && subscription.DestinationId == destinationId
                && subscription.Status == OperationalStatus.Active)
            .Select(subscription => subscription.HttpDelivery)
            .ToListAsync(cancellationToken);

    private static JsonElement? NormalizeNullableJson(JsonElement? value) =>
        value is { ValueKind: not JsonValueKind.Null } ? value : null;
}
