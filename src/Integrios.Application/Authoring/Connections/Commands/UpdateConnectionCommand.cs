using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Authoring.Subscriptions;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Connections;

public sealed record UpdateConnectionCommand(
    Guid TenantId,
    Guid Id,
    string Name,
    JsonElement Config,
    SourceVerificationInput? SourceVerification,
    DestinationAuthenticationInput? DestinationAuthentication,
    string? Environment,
    string? Description
) : IRequest<ConnectionDto?>;

internal sealed class UpdateConnectionCommandHandler(
    IConnectionRepository repository,
    IConnectionAuthoringLock authoringLock,
    IConnectorCatalog connectorCatalog,
    IDestinationAuthenticatorRegistry authSchemeRegistry,
    ISubscriptionRepository subscriptionRepository) : IRequestHandler<UpdateConnectionCommand, ConnectionDto?>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionDto?> Handle(UpdateConnectionCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync([command.Id], cancellationToken);
        Connection? existing = await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        Connector connector = await connectorCatalog.GetByIdAsync(existing.ConnectorId, cancellationToken)
            ?? throw new ConnectionValidationException("The specified connector does not exist.");

        JsonElement config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config;
        SourceVerification? sourceVerification = ConnectionSchemeValidator.ValidateSource(
            connector,
            command.SourceVerification);
        DestinationAuthentication? destinationAuthentication = ConnectionSchemeValidator.ValidateDestination(
            connector,
            command.DestinationAuthentication,
            authSchemeRegistry);

        var proposed = existing with
        {
            Config = config,
            SourceVerification = sourceVerification,
            DestinationAuthentication = destinationAuthentication,
        };
        ConnectionUsage usage = await repository.GetUsageAsync(command.TenantId, command.Id, cancellationToken);
        if (usage.Source)
            ConnectionUseValidator.ValidateSourceReadiness(proposed, connector);
        if (usage.Destination)
            ConnectionUseValidator.ValidateDestinationReadiness(proposed, connector, authSchemeRegistry);

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
                    destinationAuthentication,
                    authSchemeRegistry);
            }
        }
        catch (SubscriptionValidationException exception)
        {
            throw new ConnectionValidationException(exception.Message);
        }

        Connection? updated = await repository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Name,
            config,
            sourceVerification,
            destinationAuthentication,
            command.Environment,
            command.Description,
            cancellationToken);

        return updated is null ? null : ConnectionDto.From(updated);
    }
}
