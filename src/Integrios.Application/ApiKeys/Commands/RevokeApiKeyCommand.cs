using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.ApiKeys.Commands;

public sealed record RevokeApiKeyCommand(Guid TenantId, Guid Id) : IRequest<bool>;

public sealed class RevokeApiKeyCommandHandler(IApiKeyRepository repository)
    : IRequestHandler<RevokeApiKeyCommand, bool>
{
    public Task<bool> Handle(RevokeApiKeyCommand command, CancellationToken cancellationToken)
        => repository.RevokeAsync(command.TenantId, command.Id, cancellationToken);
}
