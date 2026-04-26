using Integrios.Application.Abstractions;
using Integrios.Domain.Tenants;
using MediatR;

namespace Integrios.Application.ApiKeys.Queries;

public sealed record GetApiKeyByIdQuery(Guid TenantId, Guid Id) : IRequest<ApiKeyResponse?>;

public sealed class GetApiKeyByIdQueryHandler(IApiKeyRepository repository)
    : IRequestHandler<GetApiKeyByIdQuery, ApiKeyResponse?>
{
    public async Task<ApiKeyResponse?> Handle(GetApiKeyByIdQuery query, CancellationToken cancellationToken)
    {
        ApiKey? key = await repository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return key is null ? null : ApiKeyResponse.From(key);
    }
}
