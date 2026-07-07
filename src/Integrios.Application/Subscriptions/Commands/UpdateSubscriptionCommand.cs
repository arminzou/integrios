using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record UpdateSubscriptionCommand(
    Guid TenantId,
    Guid TopicId,
    Guid Id,
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? TransformConfig,
    bool DlqEnabled,
    int OrderIndex,
    string? Description) : IRequest<SubscriptionResponse?>;

internal sealed class UpdateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    IConnectionRepository connectionRepository,
    IIntegrationRepository integrationRepository) : IRequestHandler<UpdateSubscriptionCommand, SubscriptionResponse?>
{
    public async Task<SubscriptionResponse?> Handle(UpdateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        await EnsureDestinationConnectionIsAllowed(command.TenantId, command.DestinationConnectionId, cancellationToken);

        var subscription = await subscriptionRepository.UpdateAsync(
            command.TenantId,
            command.TopicId,
            command.Id,
            command.Name,
            command.MatchRules,
            command.DestinationConnectionId,
            command.TransformConfig,
            command.DlqEnabled,
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
