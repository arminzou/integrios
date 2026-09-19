using Integrios.Domain.Enums;
using MediatR;

namespace Integrios.Application.Authoring.Tenants;

// Deactivating a Tenant fences all of its intake; activating resumes it. Neither changes the status
// of the Tenant's own resources, so the change is reversible.
public sealed record SetTenantStatusCommand(Guid Id, OperationalStatus Status) : IRequest<bool>;

internal sealed class SetTenantStatusCommandHandler(ITenantRepository repository)
    : IRequestHandler<SetTenantStatusCommand, bool>
{
    public Task<bool> Handle(SetTenantStatusCommand command, CancellationToken cancellationToken)
        => repository.SetStatusAsync(command.Id, command.Status, cancellationToken);
}
