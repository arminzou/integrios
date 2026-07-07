using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record CreateConnectionCommand(
    Guid TenantId,
    Guid IntegrationId,
    string Name,
    JsonElement Config,
    ConnectionAuthInput? Auth,
    string? Environment,
    string? Description) : IRequest<ConnectionResponse>;

public sealed class CreateConnectionCommandHandler(
    IConnectionRepository repository,
    IIntegrationRepository integrationRepository,
    IAuthSchemeRegistry authSchemeRegistry)
    : IRequestHandler<CreateConnectionCommand, ConnectionResponse>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionResponse> Handle(CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        Integration integration = await integrationRepository.GetByIdAsync(command.IntegrationId, cancellationToken)
            ?? throw new InvalidOperationException("The specified integration does not exist.");

        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            IntegrationId = command.IntegrationId,
            Name = command.Name,
            Config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config,
            Auth = ConnectionAuthValidator.Validate(integration, command.Auth, authSchemeRegistry),
            Status = OperationalStatus.Active,
            Environment = command.Environment,
            Description = command.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Connection created = await repository.CreateAsync(connection, cancellationToken);
        return ConnectionResponse.From(created);
    }
}
