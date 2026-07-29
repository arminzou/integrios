using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Connections;
using Integrios.Application.Integrations;
using Integrios.Domain.Common;
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
    int OrderIndex,
    string? Description) : IRequest<SubscriptionResponse?>;

internal sealed class UpdateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    IConnectionRepository connectionRepository,
    IIntegrationRepository integrationRepository,
    ITransformEvaluator transformEvaluator) : IRequestHandler<UpdateSubscriptionCommand, SubscriptionResponse?>
{
    public async Task<SubscriptionResponse?> Handle(UpdateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        SubscriptionAuthoringRules.Validate(command.MatchRules, command.TransformConfig, transformEvaluator);

        var existing = await subscriptionRepository.GetByIdAsync(
            command.TenantId,
            command.TopicId,
            command.Id,
            cancellationToken);
        if (existing is null || existing.Status == OperationalStatus.Disabled)
        {
            return null;
        }

        await EnsureDestinationConnectionIsAllowed(command.TenantId, command.DestinationConnectionId, cancellationToken);

        var subscription = await subscriptionRepository.UpdateAsync(
            command.TenantId,
            command.TopicId,
            command.Id,
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
