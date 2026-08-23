using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Connectors;
using Integrios.Domain.Common;
using Integrios.Domain.Connections;
using Integrios.Domain.Connectors;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record CreateConnectionCommand(
    Guid TenantId,
    Guid ConnectorId,
    string Name,
    JsonElement Config,
    ConnectionSchemeSelectionInput? SourceVerification,
    ConnectionSchemeSelectionInput? DestinationAuthentication,
    string? Environment,
    string? Description) : IRequest<ConnectionDto>;

internal sealed class CreateConnectionCommandHandler(
    IConnectionRepository repository,
    IConnectorCatalog connectorCatalog,
    IAuthSchemeRegistry authSchemeRegistry)
    : IRequestHandler<CreateConnectionCommand, ConnectionDto>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionDto> Handle(CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        Connector connector = await connectorCatalog.GetByIdAsync(command.ConnectorId, cancellationToken)
            ?? throw new ConnectionValidationException("The specified connector does not exist.");

        JsonElement config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            ConnectorId = command.ConnectorId,
            Name = command.Name,
            Config = config,
            SourceVerification = ConnectionSchemeSelectionValidator.ValidateSource(
                connector,
                command.SourceVerification),
            DestinationAuthentication = ConnectionSchemeSelectionValidator.ValidateDestination(
                connector,
                command.DestinationAuthentication,
                authSchemeRegistry),
            Status = OperationalStatus.Active,
            Environment = command.Environment,
            Description = command.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Connection created = await repository.CreateAsync(connection, cancellationToken);
        return ConnectionDto.From(created);
    }
}
