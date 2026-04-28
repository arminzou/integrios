using Integrios.Application.Abstractions;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.ApiKeys;

public sealed record ListApiKeysByTenantQuery(Guid TenantId, string? AfterCursor, int Limit) : IRequest<ApiKeyListResponse>;

public sealed class ListApiKeysByTenantQueryHandler(IApiKeyRepository repository)
    : IRequestHandler<ListApiKeysByTenantQuery, ApiKeyListResponse>
{
    public async Task<ApiKeyListResponse> Handle(ListApiKeysByTenantQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<ApiKey> items, string? nextCursor) = await repository.ListByTenantAsync(
            query.TenantId, query.AfterCursor, query.Limit, cancellationToken);

        return new ApiKeyListResponse
        {
            Items = items.Select(ApiKeyResponse.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
