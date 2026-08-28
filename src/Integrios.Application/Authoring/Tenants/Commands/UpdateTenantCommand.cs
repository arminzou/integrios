using MediatR;

namespace Integrios.Application.Authoring.Tenants;

public sealed record UpdateTenantCommand(
    Guid Id,
    string? Name,
    string? Description,
    string? Environment
) : IRequest<TenantDto?>;

internal sealed class UpdateTenantCommandHandler(ITenantRepository repository)
    : IRequestHandler<UpdateTenantCommand, TenantDto?>
{
    public async Task<TenantDto?> Handle(UpdateTenantCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new TenantValidationException("Name is required.", "name");

        var tenant = await repository.UpdateAsync(
            command.Id, command.Name, command.Description, command.Environment, cancellationToken);

        return tenant is null ? null : TenantDto.From(tenant);
    }
}
