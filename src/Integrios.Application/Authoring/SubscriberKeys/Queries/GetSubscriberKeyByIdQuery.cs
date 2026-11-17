using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record GetSubscriberKeyByIdQuery(Guid TenantId, Guid DestinationId, Guid Id) : IRequest<SubscriberKeyDto?>;

internal sealed class GetSubscriberKeyByIdQueryHandler(ISubscriberKeyRepository repository)
    : IRequestHandler<GetSubscriberKeyByIdQuery, SubscriberKeyDto?>
{
    public async Task<SubscriberKeyDto?> Handle(GetSubscriberKeyByIdQuery query, CancellationToken cancellationToken)
    {
        SubscriberKey? key = await repository.GetByIdAsync(query.TenantId, query.DestinationId, query.Id, cancellationToken);
        return key is null ? null : SubscriberKeyDto.From(key);
    }
}
