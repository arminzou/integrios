using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Connections;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Topics;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record CreateSubscriptionCommand(
    Guid TenantId,
    Guid TopicId,
    string? Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? MappingConfig,
    HttpDeliveryConfiguration HttpDelivery,
    int OrderIndex,
    string? Description) : IRequest<SubscriptionDto?>;

internal sealed class CreateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    ITopicRepository topicRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IConnectorReader connectorReader,
    IDestinationAuthenticatorRegistry authSchemeRegistry,
    ITransformEvaluator transformEvaluator) : IRequestHandler<CreateSubscriptionCommand, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new SubscriptionValidationException("Name is required.", "name");

        SubscriptionAuthoringRules.Validate(command.MatchRules, command.MappingConfig, command.HttpDelivery, transformEvaluator);

        var topic = await topicRepository.GetByIdAsync(command.TenantId, command.TopicId, cancellationToken);
        if (topic is null)
        {
            return null;
        }

        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            [command.DestinationConnectionId],
            cancellationToken);
        await EnsureDestinationConnectionIsAllowed(
            command.TenantId, command.DestinationConnectionId, command.HttpDelivery, cancellationToken);

        var subscription = await subscriptionRepository.CreateAsync(
            command.TenantId,
            command.TopicId,
            command.Name,
            command.MatchRules,
            command.DestinationConnectionId,
            command.MappingConfig,
            command.HttpDelivery,
            command.OrderIndex,
            command.Description,
            cancellationToken);

        return subscription is null ? null : SubscriptionDto.From(subscription);
    }

    private async Task EnsureDestinationConnectionIsAllowed(
        Guid tenantId,
        Guid destinationConnectionId,
        HttpDeliveryConfiguration httpDelivery,
        CancellationToken cancellationToken)
    {
        var connection = await connectionRepository.GetByIdAsync(tenantId, destinationConnectionId, cancellationToken);
        if (connection is null)
        {
            throw new SubscriptionValidationException(
                "The specified destination connection does not exist for this tenant.");
        }

        Connector? connector = await connectorReader.GetByIdAsync(connection.ConnectorId, cancellationToken);
        if (connector is null)
        {
            throw new SubscriptionValidationException(
                "The destination connection references a connector that does not exist.");
        }

        try
        {
            ConnectionUseValidator.ValidateDestinationAuthoring(connection, connector, authSchemeRegistry);
            HttpDeliveryConfigurationRules.ValidateAuthenticationHeaderCollisions(
                httpDelivery,
                connection.DestinationAuthentication,
                authSchemeRegistry);
        }
        catch (ConnectionValidationException exception)
        {
            throw new SubscriptionValidationException(exception.Message);
        }
    }
}
