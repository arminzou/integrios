using System.Text.Json;
using Integrios.Application.Abstractions;
using Integrios.Application.Abstractions.Auth;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record UpdateConnectionCommand(
    Guid TenantId,
    Guid Id,
    string Name,
    JsonElement Config,
    ConnectionAuthInput? Auth,
    string? Environment,
    string? Description
) : IRequest<ConnectionResponse?>;

public sealed class UpdateConnectionCommandHandler(
    IConnectionRepository repository,
    IIntegrationRepository integrationRepository,
    IAuthSchemeRegistry authSchemeRegistry) : IRequestHandler<UpdateConnectionCommand, ConnectionResponse?>
{
    private static readonly JsonElement EmptyObject = JsonSerializer.Deserialize<JsonElement>("{}");

    public async Task<ConnectionResponse?> Handle(UpdateConnectionCommand command, CancellationToken cancellationToken)
    {
        Connection? existing = await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        Integration integration = await integrationRepository.GetByIdAsync(existing.IntegrationId, cancellationToken)
            ?? throw new InvalidOperationException("The specified integration does not exist.");

        JsonElement config = command.Config.ValueKind == JsonValueKind.Undefined ? EmptyObject : command.Config;
        ConnectionAuth? auth = ConnectionAuthValidator.Validate(integration, command.Auth, authSchemeRegistry);

        Connection? updated = await repository.UpdateAsync(
            command.TenantId,
            command.Id,
            command.Name,
            config,
            auth,
            command.Environment,
            command.Description,
            cancellationToken);

        return updated is null ? null : ConnectionResponse.From(updated);
    }
}
