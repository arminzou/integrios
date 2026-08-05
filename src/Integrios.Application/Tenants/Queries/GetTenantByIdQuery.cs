using MediatR;

namespace Integrios.Application.Tenants;

public sealed record GetTenantByIdQuery(Guid Id) : IRequest<TenantDto?>;

internal sealed class GetTenantByIdQueryHandler(ITenantRepository repository)
    : IRequestHandler<GetTenantByIdQuery, TenantDto?>
{
    public async Task<TenantDto?> Handle(GetTenantByIdQuery query, CancellationToken cancellationToken)
    {
        var tenant = await repository.GetByIdAsync(query.Id, cancellationToken);
        return tenant is null ? null : TenantDto.From(tenant);
    }
}
