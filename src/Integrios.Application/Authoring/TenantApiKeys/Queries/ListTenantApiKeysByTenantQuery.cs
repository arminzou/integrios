using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.TenantApiKeys;

public sealed record ListTenantApiKeysByTenantQuery(Guid TenantId, TenantApiKeyListState? State, string? AfterCursor, int Limit) : IRequest<TenantApiKeyListDto>;

internal sealed class ListTenantApiKeysByTenantQueryHandler(ITenantApiKeyRepository repository)
    : IRequestHandler<ListTenantApiKeysByTenantQuery, TenantApiKeyListDto>
{
    public async Task<TenantApiKeyListDto> Handle(ListTenantApiKeysByTenantQuery query, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (IReadOnlyList<TenantApiKey> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.State, now, query.AfterCursor, query.Limit, cancellationToken);

        return new TenantApiKeyListDto
        {
            Items = items.Select(item => TenantApiKeyListItemDto.From(item, now)).ToList(),
            NextCursor = nextCursor,
        };
    }
}
