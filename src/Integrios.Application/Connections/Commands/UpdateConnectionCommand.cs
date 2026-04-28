using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record UpdateConnectionCommand(
    Guid TenantId,
    Guid Id,
    string Name,
    JsonElement Config,
    string? Environment,
    string? Description
) : IRequest<ConnectionResponse?>;

public sealed class UpdateConnectionCommandHandler(IConnectionRepository repository)
    : IRequestHandler<UpdateConnectionCommand, ConnectionResponse?>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionResponse?> Handle(UpdateConnectionCommand command, CancellationToken cancellationToken)
    {
        var config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config;

        Connection? updated = await repository.UpdateAsync(
            command.TenantId, command.Id, command.Name, config, command.Environment, command.Description,
            cancellationToken);

        return updated is null ? null : ConnectionResponse.From(updated);
    }
}
