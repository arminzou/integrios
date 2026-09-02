using Integrios.Domain.Entities;
using MediatR;

namespace Integrios.Application.Identity;

public sealed record GetOperatorUserQuery(Guid UserId) : IRequest<OperatorUserDto?>;

internal sealed class GetOperatorUserQueryHandler(IOperatorIdentityStore store)
    : IRequestHandler<GetOperatorUserQuery, OperatorUserDto?>
{
    public async Task<OperatorUserDto?> Handle(GetOperatorUserQuery query, CancellationToken cancellationToken)
    {
        User? user = await store.FindByIdAsync(query.UserId, cancellationToken);
        return user is null ? null : OperatorUserDto.From(user);
    }
}
