using System.Text.Json;
using Integrios.Application.Auth;
using Integrios.Application.Integrations;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record CreateConnectionCommand(
    Guid TenantId,
    Guid IntegrationId,
    string Name,
    JsonElement Config,
    ConnectionSchemeSelectionInput? SourceVerification,
    ConnectionSchemeSelectionInput? DestinationAuthentication,
    string? Environment,
    string? Description) : IRequest<ConnectionDto>;

internal sealed class CreateConnectionCommandHandler(
    IConnectionRepository repository,
    IIntegrationCatalog integrationCatalog,
    IAuthSchemeRegistry authSchemeRegistry)
    : IRequestHandler<CreateConnectionCommand, ConnectionDto>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionDto> Handle(CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        Integration integration = await integrationCatalog.GetByIdAsync(command.IntegrationId, cancellationToken)
            ?? throw new ConnectionRequestValidationException("The specified integration does not exist.");

        JsonElement config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config;
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            IntegrationId = command.IntegrationId,
            Name = command.Name,
            Config = config,
            SourceVerification = ConnectionSchemeSelectionValidator.ValidateSource(
                integration,
                command.SourceVerification),
            DestinationAuthentication = ConnectionSchemeSelectionValidator.ValidateDestination(
                integration,
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
