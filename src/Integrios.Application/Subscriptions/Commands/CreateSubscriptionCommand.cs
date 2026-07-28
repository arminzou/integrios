using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record CreateSubscriptionCommand(
    Guid TenantId,
    Guid TopicId,
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? TransformConfig,
    int OrderIndex,
    string? Description) : IRequest<SubscriptionResponse?>;

internal sealed class CreateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    ITopicRepository topicRepository,
    IConnectionRepository connectionRepository,
    IIntegrationRepository integrationRepository) : IRequestHandler<CreateSubscriptionCommand, SubscriptionResponse?>
{
    public async Task<SubscriptionResponse?> Handle(CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var topic = await topicRepository.GetByIdAsync(command.TenantId, command.TopicId, cancellationToken);
        if (topic is null)
        {
            return null;
        }

        await EnsureDestinationConnectionIsAllowed(command.TenantId, command.DestinationConnectionId, cancellationToken);

        var subscription = await subscriptionRepository.CreateAsync(
            command.TenantId,
            command.TopicId,
            command.Name,
            command.MatchRules,
            command.DestinationConnectionId,
            command.TransformConfig,
            command.OrderIndex,
            command.Description,
            cancellationToken);

        return subscription is null ? null : SubscriptionResponse.From(subscription);
    }

    private async Task EnsureDestinationConnectionIsAllowed(Guid tenantId, Guid destinationConnectionId, CancellationToken cancellationToken)
    {
        var connection = await connectionRepository.GetByIdAsync(tenantId, destinationConnectionId, cancellationToken);
        if (connection is null)
        {
            throw new SubscriptionRequestValidationException(
                "The specified destination connection does not exist for this tenant.");
        }

        Integration? integration = await integrationRepository.GetByIdAsync(connection.IntegrationId, cancellationToken);
        if (integration is null)
        {
            throw new SubscriptionRequestValidationException(
                "The destination connection references an integration that does not exist.");
        }

        if (integration.Direction == IntegrationDirection.Source)
        {
            throw new SubscriptionRequestValidationException(
                "The destination connection must use an integration whose direction is destination or both.");
        }
    }
}
