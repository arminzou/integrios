using System.Text.Json;
using Integrios.Application.Transforms;
using Integrios.Application.Connections;
using Integrios.Application.Integrations;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using Integrios.Application.Auth;
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
    string? Description) : IRequest<SubscriptionDto?>;

internal sealed class UpdateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IIntegrationCatalog integrationCatalog,
    IAuthSchemeRegistry authSchemeRegistry,
    ITransformEvaluator transformEvaluator) : IRequestHandler<UpdateSubscriptionCommand, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(UpdateSubscriptionCommand command, CancellationToken cancellationToken)
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

        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            [command.DestinationConnectionId],
            cancellationToken);
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

        return subscription is null ? null : SubscriptionDto.From(subscription);
    }

    private async Task EnsureDestinationConnectionIsAllowed(Guid tenantId, Guid destinationConnectionId, CancellationToken cancellationToken)
    {
        var connection = await connectionRepository.GetByIdAsync(tenantId, destinationConnectionId, cancellationToken);
        if (connection is null)
        {
            throw new SubscriptionValidationException(
                "The specified destination connection does not exist for this tenant.");
        }

        Integration? integration = await integrationCatalog.GetByIdAsync(connection.IntegrationId, cancellationToken);
        if (integration is null)
        {
            throw new SubscriptionValidationException(
                "The destination connection references an integration that does not exist.");
        }

        try
        {
            ConnectionUseValidator.ValidateDestinationAuthoring(connection, integration, authSchemeRegistry);
        }
        catch (ConnectionValidationException exception)
        {
            throw new SubscriptionValidationException(exception.Message);
        }
    }
}
