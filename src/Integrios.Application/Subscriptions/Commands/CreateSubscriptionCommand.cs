using System.Text.Json;
using Integrios.Application.Transforms;
using Integrios.Application.Connections;
using Integrios.Application.Integrations;
using Integrios.Application.Topics;
using Integrios.Domain.Integrations;
using Integrios.Application.Auth;
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
    string? Description) : IRequest<SubscriptionDto?>;

internal sealed class CreateSubscriptionCommandHandler(
    ISubscriptionRepository subscriptionRepository,
    ITopicRepository topicRepository,
    IConnectionRepository connectionRepository,
    IConnectionAuthoringLock authoringLock,
    IIntegrationCatalog integrationCatalog,
    IAuthSchemeRegistry authSchemeRegistry,
    ITransformEvaluator transformEvaluator) : IRequestHandler<CreateSubscriptionCommand, SubscriptionDto?>
{
    public async Task<SubscriptionDto?> Handle(CreateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        SubscriptionAuthoringRules.Validate(command.MatchRules, command.TransformConfig, transformEvaluator);

        var topic = await topicRepository.GetByIdAsync(command.TenantId, command.TopicId, cancellationToken);
        if (topic is null)
        {
            return null;
        }

        await using IAsyncDisposable lease = await authoringLock.AcquireAsync(
            [command.DestinationConnectionId],
            cancellationToken);
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
