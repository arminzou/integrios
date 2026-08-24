using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Connections;
using Integrios.Application.Connectors;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Subscriptions;

public sealed record UpdateSubscriptionCommand(
    Guid TenantId,
    Guid TopicId,
    Guid Id,
    string Name,
    JsonElement MatchRules,
    Guid DestinationConnectionId,
    JsonElement? MappingConfig,
    HttpDeliveryConfiguration HttpDelivery,
    int OrderIndex,
    string? Description) : IRequest<SubscriptionDto?>;

internal sealed class UpdateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IConnectorCatalog connectorCatalog,
    IAuthSchemeRegistry authSchemeRegistry,
    ITransformEvaluator transformEvaluator) : IRequestHandler<UpdateSubscriptionCommand, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(UpdateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        SubscriptionAuthoringRules.Validate(command.MatchRules, command.MappingConfig, command.HttpDelivery, transformEvaluator);

        var existing = await subscriptionRepository.GetByIdAsync(
            command.TenantId,
            command.TopicId,
            command.Id,
            cancellationToken);
        if (existing is null || existing.Status == OperationalStatus.Disabled)
        {
            return null;
        }

        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            [command.DestinationConnectionId],
            cancellationToken);
        await EnsureDestinationConnectionIsAllowed(
            command.TenantId, command.DestinationConnectionId, command.HttpDelivery, cancellationToken);

        var subscription = await subscriptionRepository.UpdateAsync(
            command.TenantId,
            command.TopicId,
            command.Id,
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

        Connector? connector = await connectorCatalog.GetByIdAsync(connection.ConnectorId, cancellationToken);
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
