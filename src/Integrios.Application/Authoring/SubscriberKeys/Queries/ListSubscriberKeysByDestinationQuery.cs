using Integrios.Domain.Entities;
using Integrios.Domain.ValueObjects;
using MediatR;

namespace Integrios.Application.Authoring.SubscriberKeys;

public sealed record ListSubscriberKeysByDestinationQuery(Guid TenantId, Guid DestinationId, SubscriberKeyListState? State, string? AfterCursor, int Limit) : IRequest<SubscriberKeyListDto>;

internal sealed class ListSubscriberKeysByDestinationQueryHandler(ISubscriberKeyRepository repository)
    : IRequestHandler<ListSubscriberKeysByDestinationQuery, SubscriberKeyListDto>
{
    public async Task<SubscriberKeyListDto> Handle(ListSubscriberKeysByDestinationQuery query, CancellationToken cancellationToken)
    {
        (IReadOnlyList<SubscriberKey> items, string? nextCursor) = await repository.ListByDestinationAsync(
            query.TenantId, query.DestinationId, query.State, query.AfterCursor, query.Limit, cancellationToken);

        return new SubscriberKeyListDto
        {
            Items = items.Select(SubscriberKeyListItemDto.From).ToList(),
            NextCursor = nextCursor,
        };
    }
}
