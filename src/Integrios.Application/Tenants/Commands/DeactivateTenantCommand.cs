using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Tenants;

public sealed record DeactivateTenantCommand(Guid Id) : IRequest<bool>;

public sealed class DeactivateTenantCommandHandler(ITenantRepository repository)
    : IRequestHandler<DeactivateTenantCommand, bool>
{
    public Task<bool> Handle(DeactivateTenantCommand command, CancellationToken cancellationToken)
        => repository.DeactivateAsync(command.Id, cancellationToken);
}
