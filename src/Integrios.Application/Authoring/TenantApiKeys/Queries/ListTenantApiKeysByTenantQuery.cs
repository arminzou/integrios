using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed record ListTenantApiKeysByTenantQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<TenantApiKeyListDto>;

internal sealed class ListTenantApiKeysByTenantQueryHandler(ITenantApiKeyRepository repository)
    : IRequestHandler<ListTenantApiKeysByTenantQuery, TenantApiKeyListDto>
{
    public async Task<TenantApiKeyListDto> Handle(ListTenantApiKeysByTenantQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<TenantApiKey> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.AfterCursor, query.Limit, cancellationToken);

        return new TenantApiKeyListDto
        {
            Items = items.Select(TenantApiKeyDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
