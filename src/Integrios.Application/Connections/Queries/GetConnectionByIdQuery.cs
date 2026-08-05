using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections;

public sealed record GetConnectionByIdQuery(Guid TenantId, Guid Id) : IRequest<ConnectionDto?>;

internal sealed class GetConnectionByIdQueryHandler(IConnectionRepository repository)
    : IRequestHandler<GetConnectionByIdQuery, ConnectionDto?>
{
    public async Task<ConnectionDto?> Handle(GetConnectionByIdQuery query, CancellationToken cancellationToken)
    {
        Connection? connection = await repository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return connection is null ? null : ConnectionDto.From(connection);
    }
}
