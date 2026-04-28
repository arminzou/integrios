using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Connections.Commands;

public sealed record DeactivateConnectionCommand(Guid TenantId, Guid Id) : IRequest<bool>;

public sealed class DeactivateConnectionCommandHandler(IConnectionRepository repository)
    : IRequestHandler<DeactivateConnectionCommand, bool>
{
    public async Task<bool> Handle(DeactivateConnectionCommand command, CancellationToken cancellationToken)
        => await repository.DeactivateAsync(command.TenantId, command.Id, cancellationToken);
}
