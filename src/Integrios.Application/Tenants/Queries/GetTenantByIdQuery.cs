using MediatR;

namespace Integrios.Application.Tenants;

public sealed record GetTenantByIdQuery(Guid Id) : IRequest<TenantResponse?>;

internal sealed class GetTenantByIdQueryHandler(ITenantRepository repository)
    : IRequestHandler<GetTenantByIdQuery, TenantResponse?>
{
    public async Task<TenantResponse?> Handle(GetTenantByIdQuery query, CancellationToken cancellationToken)
    {
        var tenant = await repository.GetByIdAsync(query.Id, cancellationToken);
        return tenant is null ? null : TenantResponse.From(tenant);
    }
}
