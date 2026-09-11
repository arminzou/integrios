using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Destinations;

public sealed record UpdateDestinationCommand(
    Guid TenantId,
    Guid Id,
    string? Name,
    JsonElement Configuration,
    DestinationAuthenticationInput? Authentication,
    string? Environment,
    string? Description) : IRequest<DestinationDto?>;

internal sealed class UpdateDestinationCommandHandler(
    IDestinationRepository repository,
    IDestinationAuthoringLock authoringLock,
    IConnectorReader connectorReader,
    IDestinationAuthenticatorRegistry authenticationRegistry,
    ISubscriptionRepository subscriptionRepository) : IRequestHandler<UpdateDestinationCommand, DestinationDto?>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<DestinationDto?> Handle(UpdateDestinationCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new DestinationValidationException("Name is required.", "name");

        await using IAsyncDisposable lease = await authoringLock.AcquireAsync([command.Id], cancellationToken);
        Destination? existing = await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (existing is null)
            return null;

        Connector connector = await connectorReader.GetByIdAsync(existing.ConnectorId, cancellationToken)
            ?? throw new DestinationValidationException("The specified connector does not exist.");
        JsonElement configuration = command.Configuration.ValueKind == JsonValueKind.Undefined
            ? EmptyObject
            : command.Configuration;
        DestinationAuthentication? authentication = DestinationAuthenticationValidator.Validate(
            connector,
            command.Authentication,
            authenticationRegistry);
        Destination proposed = existing with
        {
            Configuration = configuration,
            Authentication = authentication,
        };
        DestinationUseValidator.ValidateAuthoring(proposed, connector, authenticationRegistry);

        IReadOnlyList<HttpDeliveryConfiguration> activeRequests = await subscriptionRepository.ListActiveHttpDeliveriesAsync(
            command.TenantId,
            command.Id,
            cancellationToken);
        try
        {
            foreach (HttpDeliveryConfiguration request in activeRequests)
            {
                HttpDeliveryConfigurationRules.ValidateAuthenticationHeaderCollisions(
                    request,
                    authentication,
                    authenticationRegistry);
            }
        }
        catch (SubscriptionValidationException exception)
        {
            throw new DestinationValidationException(exception.Message);
        }

        Destination? updated = await repository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Name,
            configuration,
            authentication,
            command.Environment,
            command.Description,
            cancellationToken);
        return updated is null ? null : DestinationDto.From(updated);
    }
}
