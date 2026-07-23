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
    IConnectionRepository connectionRepository,
    IIntegrationRepository integrationRepository) : IRequestHandler<CreateSubscriptionCommand, SubscriptionResponse?>
{
    public async Task<SubscriptionResponse?> Handle(CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
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
            return;
        }

        Integration? integration = await integrationRepository.GetByIdAsync(connection.IntegrationId, cancellationToken);
        if (integration is null)
        {
            return;
        }

        if (integration.Direction == IntegrationDirection.Source)
        {
            throw new SubscriptionRequestValidationException(
                "The destination connection must use an integration whose direction is destination or both.");
        }
    }
}
