using System.Text.Json;
using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record UpdateSubscriptionCommand(
    Guid TenantId,
    Guid TopicId,
    Guid Id,
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    bool DlqEnabled,
    int OrderIndex,
    string? Description) : IRequest<SubscriptionResponse?>;

internal sealed class UpdateSubscriptionCommandHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<UpdateSubscriptionCommand, SubscriptionResponse?>
{
    public async Task<SubscriptionResponse?> Handle(UpdateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptionRepository.UpdateAsync(
            command.TenantId,
            command.TopicId,
            command.Id,
            command.Name,
            command.MatchRules,
            command.DestinationConnectionId,
            command.DlqEnabled,
            command.OrderIndex,
            command.Description,
            cancellationToken);

        return subscription is null ? null : SubscriptionResponse.From(subscription);
    }
}
