using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.ApiKeys;

public sealed record ListApiKeysByTenantQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<ApiKeyListDto>;

internal sealed class ListApiKeysByTenantQueryHandler(IApiKeyRepository repository)
    : IRequestHandler<ListApiKeysByTenantQuery, ApiKeyListDto>
{
    public async Task<ApiKeyListDto> Handle(ListApiKeysByTenantQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<ApiKey> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.AfterCursor, query.Limit, cancellationToken);

        return new ApiKeyListDto
        {
            Items = items.Select(ApiKeyDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
