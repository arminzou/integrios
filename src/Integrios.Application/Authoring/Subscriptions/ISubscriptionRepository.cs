using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Subscriptions;

public interface ISubscriptionRepository
{
    Task<Subscription?> CreateAsync(
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
        CancellationToken cancellationToken);

    Task<Subscription?> GetByIdAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken);

    Task<Subscription?> UpdateAsync(
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
        CancellationToken cancellationToken);

    Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken);

    // Destination authoring checks every active use before changing authentication, so header
    // ownership is validated from both directions under the same per-Destination lock.
    Task<IReadOnlyList<HttpDeliveryConfiguration>> ListActiveHttpDeliveriesAsync(
        Guid tenantId,
        Guid destinationId,
        CancellationToken cancellationToken);
}
