using Integrios.Domain.Common;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.Tenants;

public sealed record CreateTenantCommand(
    string Slug,
    string Name,
    string? Environment,
    string? Description
) : IRequest<TenantResponse>;

internal sealed class CreateTenantCommandHandler(ITenantRepository repository)
    : IRequestHandler<CreateTenantCommand, TenantResponse>
{
    public async Task<TenantResponse> Handle(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        if (!TenantSlug.IsValid(command.Slug))
        {
            throw new TenantRequestValidationException(
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
        return TenantResponse.From(created);
    }
}
