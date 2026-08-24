using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.ApiKeys;

public sealed record GetApiKeyByIdQuery(Guid TenantId, Guid Id) : IRequest<ApiKeyDto?>;

internal sealed class GetApiKeyByIdQueryHandler(IApiKeyRepository repository)
    : IRequestHandler<GetApiKeyByIdQuery, ApiKeyDto?>
{
    public async Task<ApiKeyDto?> Handle(GetApiKeyByIdQuery query, CancellationToken cancellationToken)
    {
        ApiKey? key = await repository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return key is null ? null : ApiKeyDto.From(key);
    }
}
