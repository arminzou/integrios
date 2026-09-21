using Integrios.Application.Authoring;
using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed record DeleteTenantApiKeyCommand(Guid TenantId, Guid Id) : IRequest<bool>;

internal sealed class DeleteTenantApiKeyCommandHandler(ITenantApiKeyRepository repository)
    : IRequestHandler<DeleteTenantApiKeyCommand, bool>
{
    public async Task<bool> Handle(DeleteTenantApiKeyCommand command, CancellationToken cancellationToken)
    {
        TenantApiKey? key = await repository.GetByIdAsync(command.TenantId, command.Id, cancellationToken);
        if (key is null)
            return false;
        if (key.RevokedAt is null)
            throw new AuthoringConflictException("The Tenant API key must be revoked before it can be deleted.");

        return await repository.DeleteAsync(command.TenantId, command.Id, cancellationToken);
    }
}
