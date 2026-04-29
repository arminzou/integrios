using System.Text.Json;
using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record CreateSubscriptionCommand(
    Guid TenantId,
    Guid TopicId,
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    bool DlqEnabled,
    int OrderIndex,
    string? Description) : IRequest<SubscriptionResponse>;

internal sealed class CreateSubscriptionCommandHandler(ISubscriptionRepository subscriptionRepository)
    : IRequestHandler<CreateSubscriptionCommand, SubscriptionResponse>
{
    public async Task<SubscriptionResponse> Handle(CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptionRepository.CreateAsync(
            command.TenantId,
            command.TopicId,
            command.Name,
            command.MatchRules,
            command.DestinationConnectionId,
            command.DlqEnabled,
            command.OrderIndex,
            command.Description,
            cancellationToken);

        return SubscriptionResponse.From(subscription);
    }
}
