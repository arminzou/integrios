using System.Text.Json;
using Integrios.Application.Authoring.Connectors;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Destinations;

public sealed record CreateDestinationCommand(
    Guid TenantId,
    Guid ConnectorId,
    string? Name,
    JsonElement Configuration,
    DestinationAuthenticationInput? Authentication,
    string? Environment,
    string? Description) : IRequest<DestinationDto>;

internal sealed class CreateDestinationCommandHandler(
    IDestinationRepository repository,
    IConnectorReader connectorReader,
    IDestinationAuthenticatorRegistry authenticationRegistry)
    : IRequestHandler<CreateDestinationCommand, DestinationDto>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<DestinationDto> Handle(CreateDestinationCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new DestinationValidationException("Name is required.", "name");

        Connector connector = await connectorReader.GetByIdAsync(command.ConnectorId, cancellationToken)
            ?? throw new DestinationValidationException("The specified connector does not exist.");
        JsonElement configuration = command.Configuration.ValueKind == JsonValueKind.Undefined
            ? EmptyObject
            : command.Configuration;
        var destination = new Destination
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            ConnectorId = command.ConnectorId,
            Name = command.Name,
            Configuration = configuration,
            Authentication = DestinationAuthenticationValidator.Validate(
                connector,
                command.Authentication,
                authenticationRegistry),
            Status = OperationalStatus.Active,
            Environment = command.Environment,
            Description = command.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        DestinationUseValidator.ValidateAuthoring(destination, connector, authenticationRegistry);
        Destination created = await repository.CreateAsync(destination, cancellationToken);
        return DestinationDto.From(created);
    }
}
