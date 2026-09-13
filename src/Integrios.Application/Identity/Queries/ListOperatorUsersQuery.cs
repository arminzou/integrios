using MediatR;

namespace Integrios.Application.Identity;

public sealed record ListOperatorUsersQuery : IRequest<IReadOnlyList<OperatorUserCredentialDto>>;

internal sealed class ListOperatorUsersQueryHandler(IOperatorUserQueries queries)
    : IRequestHandler<ListOperatorUsersQuery, IReadOnlyList<OperatorUserCredentialDto>>
{
    public Task<IReadOnlyList<OperatorUserCredentialDto>> Handle(
        ListOperatorUsersQuery query,
        CancellationToken cancellationToken) => queries.ListAsync(cancellationToken);
}
