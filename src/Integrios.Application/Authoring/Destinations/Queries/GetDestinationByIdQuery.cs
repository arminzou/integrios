using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Authoring.Destinations;

public sealed record GetDestinationByIdQuery(Guid TenantId, Guid Id) : IRequest<DestinationDto?>;

internal sealed class GetDestinationByIdQueryHandler(IDestinationRepository repository)
    : IRequestHandler<GetDestinationByIdQuery, DestinationDto?>
{
    public async Task<DestinationDto?> Handle(GetDestinationByIdQuery query, CancellationToken cancellationToken)
    {
        Destination? destination = await repository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return destination is null ? null : DestinationDto.From(destination);
    }
}
