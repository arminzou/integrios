using Integrios.Domain.Entities;
using Integrios.Domain.Enums;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Tenants;

public sealed record CreateTenantCommand(
    string Slug,
    string Name,
    string? Environment,
    string? Description
) : IRequest<TenantDto>;

internal sealed class CreateTenantCommandHandler(ITenantRepository repository)
    : IRequestHandler<CreateTenantCommand, TenantDto>
{
    public async Task<TenantDto> Handle(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        if (!TenantSlug.IsValid(command.Slug))
        {
            throw new TenantValidationException(
                "Tenant slug must be a lowercase DNS label of 1 to 63 characters.");
        }

        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = command.Slug,
            Name = command.Name,
            Status = OperationalStatus.Active,
            Environment = command.Environment,
            Description = command.Description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var created = await repository.CreateAsync(tenant, cancellationToken);
        return TenantDto.From(created);
    }
}
