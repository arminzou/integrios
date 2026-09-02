using System.Text.Json;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;

namespace Integrios.Application.Authoring.Subscriptions;

public interface ISubscriptionRepository
{
    Task<Subscription?> CreateAsync(
        Guid tenantId,
        Guid topicId,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        JsonElement? transformConfig,
        HttpDeliveryConfiguration httpDelivery,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken);

    Task<Subscription?> GetByIdAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Subscription> Items, string? NextCursor)> ListByTopicAsync(
        Guid tenantId,
        Guid topicId,
        OperationalStatus? status,
        string? afterCursor,
        int limit,
        CancellationToken cancellationToken);

    Task<Subscription?> UpdateAsync(
        Guid tenantId,
        Guid topicId,
        Guid id,
        string name,
        JsonElement matchRules,
        Guid destinationConnectionId,
        JsonElement? transformConfig,
        HttpDeliveryConfiguration httpDelivery,
        int orderIndex,
        string? description,
        CancellationToken cancellationToken);

    Task<bool> DeactivateAsync(Guid tenantId, Guid topicId, Guid id, CancellationToken cancellationToken);

    // Connection authoring checks every active destination use before changing authentication, so
    // header ownership is validated from both directions under the same per-Connection lock.
    Task<IReadOnlyList<HttpDeliveryConfiguration>> ListActiveHttpDeliveriesAsync(
        Guid tenantId,
        Guid destinationConnectionId,
        CancellationToken cancellationToken);
}
