using MediatR;

namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed record RevokeTenantApiKeyCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class RevokeTenantApiKeyCommandHandler(ITenantApiKeyRepository repository)
    : IRequestHandler<RevokeTenantApiKeyCommand, bool>
{
    public Task<bool> Handle(RevokeTenantApiKeyCommand command, CancellationToken cancellationToken)
        => repository.RevokeAsync(command.TenantId, command.Id, cancellationToken);
}
