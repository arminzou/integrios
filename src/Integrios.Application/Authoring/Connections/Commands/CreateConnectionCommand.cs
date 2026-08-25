using System.Text.Json;
using Integrios.Application.Delivery;
using Integrios.Application.Authoring.Connectors;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.Connections;

public sealed record CreateConnectionCommand(
    Guid TenantId,
    Guid ConnectorId,
    string Name,
    JsonElement Config,
    SourceVerificationInput? SourceVerification,
    DestinationAuthenticationInput? DestinationAuthentication,
    string? Environment,
    string? Description) : IRequest<ConnectionDto>;

internal sealed class CreateConnectionCommandHandler(
    IConnectionRepository repository,
    IConnectorReader connectorReader,
    IDestinationAuthenticatorRegistry authSchemeRegistry)
    : IRequestHandler<CreateConnectionCommand, ConnectionDto>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionDto> Handle(CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        Connector connector = await connectorReader.GetByIdAsync(command.ConnectorId, cancellationToken)
            ?? throw new ConnectionValidationException("The specified connector does not exist.");

        JsonElement config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            ConnectorId = command.ConnectorId,
            Name = command.Name,
            Config = config,
            SourceVerification = ConnectionSchemeValidator.ValidateSource(
                connector,
                command.SourceVerification),
            DestinationAuthentication = ConnectionSchemeValidator.ValidateDestination(
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
