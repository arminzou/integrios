using MediatR;

namespace Integrios.Application.Authoring.Connections;

public sealed record DeactivateConnectionCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class DeactivateConnectionCommandHandler(IConnectionRepository repository)
    : IRequestHandler<DeactivateConnectionCommand, bool>
{
    public async Task<bool> Handle(DeactivateConnectionCommand command, CancellationToken cancellationToken)
        => await repository.DeactivateAsync(command.TenantId, command.Id, cancellationToken);
}
