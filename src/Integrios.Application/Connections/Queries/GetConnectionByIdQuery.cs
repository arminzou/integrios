using Integrios.Application.Abstractions;
using Integrios.Domain.Integrations;
using MediatR;

namespace Integrios.Application.Connections.Queries;

public sealed record GetConnectionByIdQuery(Guid TenantId, Guid Id) : IRequest<ConnectionResponse?>;

public sealed class GetConnectionByIdQueryHandler(IConnectionRepository repository)
    : IRequestHandler<GetConnectionByIdQuery, ConnectionResponse?>
{
    public async Task<ConnectionResponse?> Handle(GetConnectionByIdQuery query, CancellationToken cancellationToken)
    {
        Connection? connection = await repository.GetByIdAsync(query.TenantId, query.Id, cancellationToken);
        return connection is null ? null : ConnectionResponse.From(connection);
    }
}
