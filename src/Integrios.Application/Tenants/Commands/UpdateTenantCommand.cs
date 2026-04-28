using Integrios.Application.Abstractions;
using MediatR;

namespace Integrios.Application.Tenants;

public sealed record UpdateTenantCommand(
    Guid Id,
    string Name,
    string? Description,
    string? Environment
) : IRequest<TenantResponse?>;

public sealed class UpdateTenantCommandHandler(ITenantRepository repository)
    : IRequestHandler<UpdateTenantCommand, TenantResponse?>
{
    public async Task<TenantResponse?> Handle(UpdateTenantCommand command, CancellationToken cancellationToken)
    {
        var tenant = await repository.UpdateAsync(
            command.Id, command.Name, command.Description, command.Environment, cancellationToken);

        return tenant is null ? null : TenantResponse.From(tenant);
    }
}
