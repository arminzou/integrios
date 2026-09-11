using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Destinations;
using Integrios.Application.Transforms;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Subscriptions;

public sealed record UpdateSubscriptionCommand(
    Guid TenantId,
    Guid TopicId,
    Guid Id,
    string? Name,
    JsonElement MatchRules,
    Guid DestinationId,
    JsonElement? MappingConfig,
    HttpDeliveryConfiguration HttpDelivery,
    HttpSuccessRule? HttpSuccess,
    int OrderIndex,
    string? Description) : IRequest<SubscriptionDto?>;

internal sealed class UpdateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    IDestinationRepository destinationRepository,
    IDestinationAuthoringLock authoringLock,
    IConnectorReader connectorReader,
    IDestinationAuthenticatorRegistry authSchemeRegistry,
    ITransformEvaluator transformEvaluator) : IRequestHandler<UpdateSubscriptionCommand, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(UpdateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new SubscriptionValidationException("Name is required.", "name");

        SubscriptionAuthoringRules.Validate(
            command.MatchRules,
            command.MappingConfig,
            command.HttpDelivery,
            command.HttpSuccess,
            transformEvaluator);

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
            [command.DestinationId],
            cancellationToken);
        await EnsureDestinationIsAllowed(
            command.TenantId, command.DestinationId, command.HttpDelivery, cancellationToken);

        var subscription = await subscriptionRepository.UpdateAsync(
            command.TenantId,
            command.TopicId,
            command.Id,
            command.Name,
            command.MatchRules,
            command.DestinationId,
            command.MappingConfig,
            command.HttpDelivery,
            command.HttpSuccess,
            command.OrderIndex,
            command.Description,
            cancellationToken);

        return subscription is null ? null : SubscriptionDto.From(subscription);
    }

    private async Task EnsureDestinationIsAllowed(
        Guid tenantId,
        Guid destinationId,
        HttpDeliveryConfiguration httpDelivery,
        CancellationToken cancellationToken)
    {
        var destination = await destinationRepository.GetByIdAsync(tenantId, destinationId, cancellationToken);
        if (destination is null)
        {
            throw new SubscriptionValidationException(
                "The specified Destination does not exist for this tenant.");
        }

        Connector? connector = await connectorReader.GetByIdAsync(destination.ConnectorId, cancellationToken);
        if (connector is null)
        {
            throw new SubscriptionValidationException(
                "The Destination references a Connector that does not exist.");
        }

        try
        {
            DestinationUseValidator.ValidateAuthoring(destination, connector, authSchemeRegistry);
            HttpDeliveryConfigurationRules.ValidateAuthenticationHeaderCollisions(
                httpDelivery,
                destination.Authentication,
                authSchemeRegistry);
        }
        catch (DestinationValidationException exception)
        {
            throw new SubscriptionValidationException(exception.Message);
        }
    }
}
