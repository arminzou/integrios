using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Integrations;
using Integrios.Domain.Integrations;
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
) : IRequest<ConnectionResponse?>;

internal sealed class UpdateConnectionCommandHandler(
    IConnectionRepository repository,
    IConnectionAuthoringLock authoringLock,
    IIntegrationCatalog integrationCatalog,
    IAuthSchemeRegistry authSchemeRegistry) : IRequestHandler<UpdateConnectionCommand, ConnectionResponse?>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionResponse?> Handle(UpdateConnectionCommand command, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable lease = await authoringLock.AcquireAsync([command.Id], cancellationToken);
        Connection? existing = await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        Integration integration = await integrationCatalog.GetByIdAsync(existing.IntegrationId, cancellationToken)
            ?? throw new ConnectionRequestValidationException("The specified integration does not exist.");

        JsonElement config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config;
        ConnectionSchemeSelection? sourceVerification = ConnectionSchemeSelectionValidator.ValidateSource(
            integration,
            command.SourceVerification);
        ConnectionSchemeSelection? destinationAuthentication = ConnectionSchemeSelectionValidator.ValidateDestination(
            integration,
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
            ConnectionRoleValidator.ValidateSource(proposed, integration);
        if (usage.Destination)
            ConnectionRoleValidator.ValidateDestination(proposed, integration, authSchemeRegistry);

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

        return updated is null ? null : ConnectionResponse.From(updated);
    }
}
