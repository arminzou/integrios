using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Domain.Common;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections.Commands;

public sealed record CreateConnectionCommand(
    Guid TenantId,
    Guid IntegrationId,
    string Name,
    JsonElement Config,
    string? Environment,
    string? Description
) : IRequest<ConnectionResponse>;

public sealed class CreateConnectionCommandHandler(IConnectionRepository repository)
    : IRequestHandler<CreateConnectionCommand, ConnectionResponse>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionResponse> Handle(CreateConnectionCommand command, CancellationToken cancellationToken)
    {
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            IntegrationId = command.IntegrationId,
            Name = command.Name,
            Config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config,
            SecretReferences = EmptyObject,
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
