using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Connectors;
using Integrios.Application.Subscriptions;
using Integrios.Domain.Connections;
using Integrios.Domain.Connectors;
using Integrios.Domain.Subscriptions;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record UpdateConnectionCommand(
    Guid TenantId,
    Guid Id,
    string Name,
    JsonElement Config,
    ConnectionSchemeSelectionInput? SourceVerification,
    ConnectionSchemeSelectionInput? DestinationAuthentication,
    string? Environment,
    string? Description
) : IRequest<ConnectionDto?>;

internal sealed class UpdateConnectionCommandHandler(
    IConnectionRepository repository,
    IConnectionAuthoringLock authoringLock,
    IConnectorCatalog connectorCatalog,
    IAuthSchemeRegistry authSchemeRegistry,
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
        ConnectionSchemeSelection? sourceVerification = ConnectionSchemeSelectionValidator.ValidateSource(
            connector,
            command.SourceVerification);
        ConnectionSchemeSelection? destinationAuthentication = ConnectionSchemeSelectionValidator.ValidateDestination(
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
