using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.TenantApiKeys;

public sealed record GetTenantApiKeyByIdQuery(Guid TenantId, Guid Id) : IRequest<TenantApiKeyDto?>;

internal sealed class GetTenantApiKeyByIdQueryHandler(ITenantApiKeyRepository repository)
    : IRequestHandler<GetTenantApiKeyByIdQuery, TenantApiKeyDto?>
{
    public async Task<TenantApiKeyDto?> Handle(GetTenantApiKeyByIdQuery query, CancellationToken cancellationToken)
    {
        TenantApiKey? key = await repository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return key is null ? null : TenantApiKeyDto.From(key);
    }
}
