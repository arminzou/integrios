using MediatR;

namespace Integrios.Application.Authoring.Tenants;

public sealed record ActivateTenantCommand(Guid Id) : IRequest<bool>;

internal sealed class ActivateTenantCommandHandler(ITenantRepository repository)
    : IRequestHandler<ActivateTenantCommand, bool>
{
    public Task<bool> Handle(ActivateTenantCommand command, CancellationToken cancellationToken)
        => repository.ActivateAsync(command.Id, cancellationToken);
}
